using System.Collections.ObjectModel;
using System.Diagnostics;
using Dictate.Core;
using Dictate.Core.Dictation;
using Dictate.Core.Transcription;
using GrokVoice.Bot;
using GrokVoice.Input;
using GrokVoice.Platform;
using Microsoft.UI.Dispatching;

namespace GrokVoice;

/// <summary>
/// Wires the keys, microphone, Grok speech to text and the two destinations together, and keeps the
/// recording pill in step. Right Alt dictates at the cursor; a long press of the Copilot key
/// dictates an instruction for the main bot, and a tap opens Grok Bot.
/// Created on the UI thread; its events are raised there too.
/// </summary>
internal sealed class DictationHost : IDisposable
{
    private const int MaxLogLines = 300;
    // Windows' own Copilot key events that arrive this soon after the hook saw the key are the same press.
    private const long ProviderDuplicateMs = 1500;

    private readonly DispatcherQueue _ui;
    private readonly MicrophoneSource _mic = new();
    private readonly RoutingTextSink _sink;
    private readonly KeyStateMachine _altMachine = new();
    private readonly HoldDetector _alt = new();
    private readonly HoldDetector _copilot = new(holdThreshold: TimeSpan.FromMilliseconds(300));
    private readonly KeyboardHook _hook;
    private readonly DictationController _controller;
    private readonly RecordingPill _pill = new();
    private readonly TaskbarGlyph _glyph;
    private readonly DispatcherQueueTimer _errorTimer;
    private bool _error, _awaitingBot;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _sessionStart;
    private int _levelPending;
    private float _sessionPeak;
    // Which key owns the dictation in progress, so the other key can't take it over midway.
    private KeySource? _owner;

    private enum KeySource { RightAlt, Copilot }

    public DictationHost(DispatcherQueue ui)
    {
        _ui = ui;
        GrokVoice.Log.Sink = Write;
        TaskbarGlyph.Error = Write;
        _glyph = new TaskbarGlyph();
        Bot = new BotSender(this);
        var keyboard = new KeyboardTextSink { IsOwnWindow = h => h == _pill.Handle || h == _glyph.Handle || h == MainWindowHandle };
        Words = new GrokVoice.Words.WordDictionary();
        Polisher = new GrokVoice.Words.TextPolisher(() => Keys.Get(), Words, () => Settings.PolishEnabled, Usage);
        _sink = new RoutingTextSink(keyboard, Bot, Polisher.PolishAsync, WithScreenContext);
        Polisher.Context = () => _context;
        Polisher.ToBot = () => _sink.Current == DictationTarget.Bot;
        Polisher.UseAppStyle = () => Settings.AppStyle;
        Polisher.UseScreen = () => Settings.ScreenContext;

        // Before the controller, which opens the microphone straight away when it's kept warm.
        _mic.DeviceId = Settings.MicrophoneId is { Length: > 0 } micId ? micId : null;
        _mic.Faulted += m => _ui.TryEnqueue(() => OnFailed(m));
        _mic.LevelChanged += OnLevel;
        if (!Settings.KeepMicWarm) Task.Run(_mic.Prepare);

        // The mic now opens on key-down instead of staying open; switch existing installs over once.
        if (!Settings.MicClosedDefaulted)
        {
            Settings.KeepMicWarm = false;
            Settings.MicClosedDefaulted = true;
        }
        _controller = new DictationController(_mic, _sink, CreateTranscriber, Settings.KeepMicWarm);
        // A dictation key went down: open the mic now so the first words of a hold are kept. Off the
        // keyboard hook's thread, which must return at once.
        _alt.Pressed += () => Task.Run(() => _controller.Prime(TimeSpan.FromSeconds(1.5)));
        _copilot.Pressed += () => Task.Run(() => _controller.Prime(TimeSpan.FromSeconds(1.5)));
        _controller.StatusChanged += s => _ui.TryEnqueue(() => OnStatus(s));
        _controller.TextChanged += t => _ui.TryEnqueue(() => _pill.SetTranscript(t));
        _controller.Inserted += t => _ui.TryEnqueue(() => OnInserted(t));
        _controller.Failed += m => _ui.TryEnqueue(() => OnFailed(m));
        _controller.NothingHeard += d => Write($"heard nothing in {d.TotalSeconds:0.0} s of audio, loudest {_sessionPeak:P0}");

        _altMachine.ActionRequested += a => OnAction(a, KeySource.RightAlt);
        _altMachine.StateChanged += (_, to) => _ui.TryEnqueue(() => OnAltState(to));
        _alt.Gesture += e => _ui.TryEnqueue(() => OnAltGesture(e));
        _copilot.Gesture += e => _ui.TryEnqueue(() => OnCopilotGesture(e, "hook"));

        Bot.Replied += (name, reply) => _ui.TryEnqueue(() => OnBotReply(name, reply));
        Bot.NoReply += name => _ui.TryEnqueue(() =>
        {
            Write($"bot   no reply from {name} within 5 minutes");
            _awaitingBot = false;
            UpdateGlyph();
        });
        GrokVoice.Words.Benchmark.StartIfRequested(() => Keys.Get(), () => ElevenKeys.Get(), Settings.Language, Words, Usage);
        Updates = new Updater(this);
        Updates.Start();
        _pill.Clicked += () =>
        {
            if (_controller.Status == DictationStatus.Idle) OpenGrokBot();
        };

        // The taskbar glyph: Murmur's resting UI. Cards (Jeeves, tips, errors) rise from just above it.
        _errorTimer = _ui.CreateTimer();
        _errorTimer.IsRepeating = false;
        _errorTimer.Interval = TimeSpan.FromSeconds(8);
        _errorTimer.Tick += (_, _) => { _error = false; UpdateGlyph(); };
        _glyph.Enabled = Settings.RestingPill;
        _pill.Resting = false;
        _pill.Anchor = () => _glyph.IsShowing ? _glyph.Bounds : null;
        _pill.Hidden += UpdateGlyph;
        _pill.InsertRequested += InsertReply;
        _glyph.Clicked += () =>
        {
            if (_pill.ShowingConversation) _pill.Hide();
            else if (_controller.Status == DictationStatus.Idle) OpenGrokBot();
        };
        _glyph.Hovering += on =>
        {
            if (on && _controller.Status == DictationStatus.Idle && !_awaitingBot) _pill.ShowTip($"${Usage.Today.Cost:0.00} today");
            else _pill.HideTip();
        };
        _glyph.MenuRequested += ShowMenu;

        _hook = new KeyboardHook(Write) { RightAltEnabled = Settings.RightAltEnabled };
        _hook.RightAltDown += _alt.Down;
        _hook.RightAltUp += _alt.Up;
        _hook.RightAltAbandoned += _alt.Abandon;
        _hook.CopilotDown += (releaseWin, releaseShift) =>
        {
            _copilot.Down();
            _ui.TryEnqueue(() => ReleaseCopilotModifiers(releaseWin, releaseShift));
        };
        _hook.CopilotUp += _copilot.Up;
        Write($"ready ({(_hook.IsActive ? "keyboard hook on" : "no keyboard hook")})");

        Task.Run(() =>
        {
            try
            {
                Launcher.Prepare();
            }
            catch (Exception e)
            {
                Write($"error finding Grok Bot: {e.Message}");
            }
        });
    }

    /// <summary>Sends Copilot key instructions to the main bot.</summary>
    public BotSender Bot { get; }
    public GrokVoice.Words.WordDictionary Words { get; }
    public GrokVoice.Words.TextPolisher Polisher { get; }

    /// <summary>What each xAI call cost, for the usage totals in settings.</summary>
    public GrokVoice.Usage.UsageLog Usage { get; } = new(Windows.Storage.ApplicationData.Current.LocalFolder.Path);

    /// <summary>Whether Murmur's glyph shows in the taskbar (the setting kept its old name, "RestingPill").</summary>
    public bool RestingPill
    {
        get => Settings.RestingPill;
        set
        {
            Settings.RestingPill = value;
            _glyph.Enabled = value;
        }
    }

    /// <summary>Paused from the glyph's menu: Right Alt and the Copilot key hold do nothing until resumed.</summary>
    public bool Paused { get; private set; }

    /// <summary>The glyph's menu asked for settings.</summary>
    public event Action? SettingsRequested;
    /// <summary>The glyph's menu asked to quit.</summary>
    public event Action? QuitRequested;

    private void ShowMenu(int x, int y)
    {
        _pill.HideTip();
        const int pause = 1, settings = 2, quit = 3;
        var chosen = _glyph.ShowMenu(x, y,
        [
            (pause, Paused ? "Resume Murmur" : "Pause Murmur", false),
            (settings, "Settings", false),
            (0, "", false),
            (quit, "Quit Murmur", false),
        ]);
        switch (chosen)
        {
            case pause:
                Paused = !Paused;
                _hook.RightAltEnabled = Settings.RightAltEnabled && !Paused;
                Write(Paused ? "paused" : "resumed");
                UpdateGlyph();
                break;
            case settings:
                SettingsRequested?.Invoke();
                break;
            case quit:
                QuitRequested?.Invoke();
                break;
        }
    }

    /// <summary>Insert under a Jeeves reply: types it where the cursor is (the card never takes focus).</summary>
    private void InsertReply(string text)
    {
        if (_controller.Status != DictationStatus.Idle) return;
        _sink.Keyboard.RememberTarget();
        _ = Task.Run(async () =>
        {
            try
            {
                await _sink.Keyboard.InsertAsync(text.Trim(), CancellationToken.None);
                Write($"typed reply, {text.Length} chars");
            }
            catch (Exception e)
            {
                _ui.TryEnqueue(() => OnFailed(e.Message));
            }
        });
    }

    /// <summary>Sets the glyph from what Murmur is doing: violet for dictation, amber for the bot.</summary>
    private void UpdateGlyph()
    {
        var bot = _owner == KeySource.Copilot;
        var dictating = bot || _altMachine.State is DictationState.PushToTalk or DictationState.HandsFree;
        _glyph.Mode = Paused ? Dictate.Core.Glyph.GlyphMode.Paused
            : _error ? Dictate.Core.Glyph.GlyphMode.Error
            : _controller.Status == DictationStatus.Listening && dictating
                ? (bot ? Dictate.Core.Glyph.GlyphMode.BotListen : Dictate.Core.Glyph.GlyphMode.Listen)
            : _controller.Status == DictationStatus.Transcribing
                ? (bot ? Dictate.Core.Glyph.GlyphMode.BotWork : Dictate.Core.Glyph.GlyphMode.Work)
            : _awaitingBot ? Dictate.Core.Glyph.GlyphMode.BotWork
            : _pill.ShowingConversation ? Dictate.Core.Glyph.GlyphMode.Reply
            : Dictate.Core.Glyph.GlyphMode.Rest;
    }

    /// <summary>
    /// The listening card: only needed without the glyph, or to answer in an open Jeeves conversation.
    /// Otherwise the glyph alone shows it.
    /// </summary>
    private void ShowListeningCard()
    {
        if (!_glyph.IsShowing || (ListeningMode == RecordingPill.ListenMode.Bot && _pill.ShowingConversation))
            _pill.ShowListening(_sink.Keyboard.Target, ListeningMode);
        else _pill.HideTip();
    }

    private void ShowAvatarCard()
    {
        if (!_glyph.IsShowing) _pill.ShowAvatar(BotAvatarClip);
    }

    public AppSettings Settings { get; } = new();
    public ApiKeyStore Keys { get; } = new();
    /// <summary>A pending accuracy benchmark waits for this key, so start it once the key is saved.</summary>
    public void OnElevenKeySaved() => GrokVoice.Words.Benchmark.StartIfRequested(() => Keys.Get(), () => ElevenKeys.Get(), Settings.Language, Words, Usage);

    /// <summary>ElevenLabs key, for Scribe speech-to-text when chosen in settings.</summary>
    public ApiKeyStore ElevenKeys { get; } = new("elevenlabs-api-key", "Grok Voice ElevenLabs");
    public ObservableCollection<string> Log { get; } = new();
    public DictationStatus Status => _controller.Status;

    public Updater Updates { get; }

    private TimeSpan _lastActive;

    /// <summary>Nothing in progress and nothing used for a minute, so an update won't interrupt.</summary>
    public bool IdleForUpdate => _controller.Status == DictationStatus.Idle && _owner is null
        && _clock.Elapsed - _lastActive > TimeSpan.FromMinutes(1);

    /// <summary>After an in-place update, a short note on the pill.</summary>
    public void ShowUpdated() => _ui.TryEnqueue(() =>
    {
        var v = Updater.Current;
        Write($"updated to {v}");
        _pill.ShowInfo($"Murmur updated to {v.ToString(3)}", IntPtr.Zero);
    });
    public string? LastText { get; private set; }
    public IntPtr MainWindowHandle { get; set; }

    public event Action<DictationStatus>? StatusChanged;
    public event Action<string>? Inserted;
    public event Action<string>? Failed;
    /// <summary>
    /// Whether Escape should cancel right now. Only in hands-free: claiming a hotkey while the
    /// Copilot key is held could disturb the hold, and letting go already ends push-to-talk.
    /// </summary>
    public event Action<bool>? CancelAvailableChanged;

    public bool CancelAvailable =>
        _controller.Status == DictationStatus.Listening && _altMachine.State == DictationState.HandsFree;

    /// <summary>What has to be set up before dictation works, or null when it's ready.</summary>
    public string? SetupNeeded => string.IsNullOrWhiteSpace(Keys.Get()) && !(Settings.SpeechEngine == "elevenlabs" && !string.IsNullOrWhiteSpace(ElevenKeys.Get()))
        ? "Add your xAI API key in Murmur's settings to start dictating."
        : null;

    /// <summary>Whether Right Alt dictates. Off lets Right Alt through untouched (for Wispr Flow, say).</summary>
    public bool RightAltEnabled
    {
        get => Settings.RightAltEnabled;
        set
        {
            Settings.RightAltEnabled = value;
            _hook.RightAltEnabled = value && !Paused;
        }
    }

    /// <summary>The chosen microphone's id, or empty for Windows' default. Takes effect straight away.</summary>
    public string MicrophoneId
    {
        get => Settings.MicrophoneId;
        set
        {
            Settings.MicrophoneId = value;
            _mic.DeviceId = value.Length == 0 ? null : value;
            if (_controller.Status != DictationStatus.Idle) return;
            var wasRunning = _mic.IsRunning;
            _mic.Stop();
            if (wasRunning) _mic.Start();
            else Task.Run(_mic.Prepare);
        }
    }

    public bool KeepMicWarm
    {
        get => Settings.KeepMicWarm;
        set
        {
            Settings.KeepMicWarm = value;
            _controller.KeepMicWarm = value;
        }
    }

    /// <summary>
    /// Windows' own Copilot key event (fast path or a URI launch). The keyboard hook normally sees the
    /// key first; these only count when it didn't, such as the press that started the app. UI thread.
    /// </summary>
    public void OnProviderKey(KeyEvent e, string source)
    {
        if (e == KeyEvent.Cancel)
        {
            Cancel();
            return;
        }
        if (_hook.IsActive && (_hook.CopilotHeld || Environment.TickCount64 - _hook.LastCopilotDown < ProviderDuplicateMs))
        {
            Write($"key   copilot {e} from {source}: same press as the hook's");
            return;
        }
        OnCopilotGesture(e, source);
    }

    /// <summary>Escape during hands-free.</summary>
    public void Cancel()
    {
        Write("key   escape");
        _altMachine.Handle(KeyEvent.Cancel);
    }

    /// <summary>Checks an API key by streaming half a second of silence to Grok. Returns null when it works.</summary>
    public async Task<string?> TestKeyAsync(string key)
    {
        var started = _clock.Elapsed;
        try
        {
            await using var transcriber = new XaiStreamingTranscriber(new XaiSttOptions { ApiKey = key, Language = Settings.Language });
            await transcriber.ConnectAsync(CancellationToken.None);
            await transcriber.SendAudioAsync(new byte[16000], CancellationToken.None);
            await transcriber.FinishAsync(CancellationToken.None);
            Write($"test  ok in {(_clock.Elapsed - started).TotalMilliseconds:0} ms");
            return null;
        }
        catch (Exception e)
        {
            var message = e is TranscriptionException ? e.Message
                : e is OperationCanceledException or TimeoutException ? "Grok didn't answer in time."
                : $"Couldn't reach Grok: {e.Message}";
            Write($"test  failed: {message}");
            return message;
        }
    }

    public void Write(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        Debug.WriteLine(entry);
        AppendToFile(entry);
        if (_ui.HasThreadAccess) Add(entry);
        else _ui.TryEnqueue(() => Add(entry));

        void Add(string e)
        {
            Log.Insert(0, e);
            if (Log.Count > MaxLogLines) Log.RemoveAt(Log.Count - 1);
        }
    }

    /// <summary>The diagnostics log on disk, so it can be read after the fact: LocalState\grokvoice.log in the package folder.</summary>
    public static string LogPath { get; } = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "grokvoice.log");

    private static readonly object FileGate = new();

    private static void AppendToFile(string entry)
    {
        try
        {
            lock (FileGate)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2_000_000) File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd} {entry}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never break dictation.
        }
    }

    // Throws with a message for the user when there's no API key yet.
    private ITranscriber CreateTranscriber()
    {
        if (SetupNeeded is { } setup) throw new TranscriptionException(setup);
        // The whole clip in one request: a short pause after letting go, for better accuracy than streaming.
        _diagnostics = NewDiagnosticsFolder();
        // No key terms for now: a hundred of them from the imported dictionary can pull the recognizer
        // towards unrelated words. The clean-up pass still uses the dictionary.
        Write($"stt   {(Settings.SpeechEngine == "elevenlabs" && ElevenKeys.Get() is { Length: > 0 } ? "ElevenLabs Scribe" : "Grok")}");
        if (Settings.SpeechEngine == "elevenlabs" && ElevenKeys.Get() is { Length: > 0 } eleven)
            return new ElevenLabsTranscriber(new ElevenLabsSttOptions { ApiKey = eleven, Language = Settings.Language })
            {
                Billed = (model, seconds) => Usage.AddTranscription(model, seconds),
                DiagnosticsFolder = _diagnostics,
            };
        return new XaiRestTranscriber(new XaiSttOptions { ApiKey = Keys.Get()!, Language = Settings.Language }, null,
            Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "last-dictation.wav"))
        {
            Billed = (model, seconds) => Usage.AddTranscription(model, seconds),
            DiagnosticsFolder = _diagnostics,
        };
    }

    /// <summary>Recent dictations, one folder each (audio.wav, stt.json, result.json), newest 50 kept.</summary>
    public static string DiagnosticsRoot { get; } = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "diagnostics");
    private string? _diagnostics;

    private static string NewDiagnosticsFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsRoot);
            foreach (var old in Directory.GetDirectories(DiagnosticsRoot).OrderByDescending(d => d).Skip(49))
                try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        return Path.Combine(DiagnosticsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
    }

    private Task<GrokVoice.Context.DictationContext>? _context;

    /// <summary>
    /// Shapes an instruction for the bot. A quick capture ("remind me…", "add … to my list") is
    /// marked so the bot files it and confirms briefly. An instruction about something on screen
    /// ("summarize this", "draft a reply", "tell him") gets the selection and the nearby text
    /// appended, so the bot knows what "this" is.
    /// </summary>
    private string WithScreenContext(string text)
    {
        var capture = GrokVoice.Context.AppAwareness.IsCapture(text);
        var message = capture
            ? $"Quick capture from Murmur. File this as a reminder, task or list item as fits, then confirm in one short line:\n{text}"
            : text;
        if (!Settings.ScreenContext || _context is not { IsCompletedSuccessfully: true } done) return message;
        var context = done.Result;
        if (context.Text.Length == 0 && context.Selection.Length == 0) return message;
        // A capture only takes the screen along when it names it ("remind me to answer this email").
        var wanted = capture
            ? GrokVoice.Context.AppAwareness.NamesScreen(text)
            : context.Selection.Length > 0 || context.IsReplyApp
              || GrokVoice.Context.AppAwareness.ActsOnScreen(text) || GrokVoice.Context.AppAwareness.RefersToScreen(text);
        if (!wanted) return message;
        var where = context.Title.Length > 0 ? $"{context.Process}: {context.Title}" : context.Process;
        var result = new System.Text.StringBuilder(message).Append("\n\n---\n");
        if (context.Selection.Length > 0)
            result.Append($"What I have selected on my screen ({where}):\n{context.Selection}\n\n");
        if (context.Text.Length > 0)
            result.Append($"{(context.Selection.Length > 0 ? "Other text" : "Text")} on my screen ({where}, read {context.Source}):\n{context.Text}");
        Write($"bot   {(capture ? "capture, " : "")}screen context: {context.Selection.Length} selected + {context.Text.Length} chars ({context.Source})");
        return result.ToString().TrimEnd();
    }

    private void SaveResult(string heard, string delivered)
    {
        if (_diagnostics is null) return;
        try
        {
            Directory.CreateDirectory(_diagnostics);
            var result = new Dictionary<string, object?>
            {
                ["target"] = _sink.Current.ToString(),
                ["heard"] = heard,
                ["clean_up_answer"] = Polisher.LastAnswer,
                ["clean_up"] = Polisher.LastNote,
                ["delivered"] = delivered,
                ["microphone"] = _mic.Description,
                ["app"] = _context is { IsCompletedSuccessfully: true } c ? new Dictionary<string, object?>
                {
                    ["process"] = c.Result.Process,
                    ["title"] = c.Result.Title,
                    ["kind"] = c.Result.Kind.ToString(),
                    ["source"] = c.Result.Source,
                    ["read_ms"] = c.Result.Milliseconds,
                    ["chars"] = c.Result.Text.Length,
                } : null,
                ["style"] = Polisher.LastStyle,
                ["screen_text_used"] = Polisher.LastContext,
                ["dictionary_entries"] = Words.Entries.Count,
                ["version"] = Windows.ApplicationModel.Package.Current.Id.Version is var v ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : "",
            };
            File.WriteAllText(Path.Combine(_diagnostics, "result.json"),
                System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }


    private void OnAltGesture(KeyEvent e)
    {
        if (_owner == KeySource.Copilot)
        {
            Write($"key   right alt {e}: ignored, a bot instruction is in progress");
            return;
        }
        _hook.RightAltHolding = e == KeyEvent.HoldStart;
        Write($"key   right alt {e}");
        _altMachine.Handle(e);
    }

    private void OnCopilotGesture(KeyEvent e, string source)
    {
        Write($"key   copilot {e} ({source})");
        switch (e)
        {
            case KeyEvent.Tap when _owner is null:
                OpenGrokBot();
                break;
            case KeyEvent.HoldStart when _owner is null:
                OnAction(DictationAction.StartCapture, KeySource.Copilot);
                break;
            case KeyEvent.HoldEnd when _owner == KeySource.Copilot:
                OnAction(DictationAction.StopAndTranscribe, KeySource.Copilot);
                break;
            case KeyEvent.Tap or KeyEvent.HoldStart:
                Write("key   copilot ignored, a dictation is in progress");
                break;
        }
    }

    private void OpenGrokBot()
    {
        Task.Run(() =>
        {
            var started = _clock.Elapsed;
            try
            {
                if (Launcher.TargetOrDefault() is not { } target)
                {
                    Write("open  Grok Bot isn't installed (not in the Start menu)");
                    return;
                }
                var how = Launcher.Open(target);
                Write($"open  {how} {target.Name} in {(_clock.Elapsed - started).TotalMilliseconds:0} ms");
            }
            catch (Exception ex)
            {
                Write($"error {ex.Message}");
            }
        });
    }

    // The Copilot key arrives as Win + Shift + F23; let go of what got through before F23. The
    // unassigned key between Win's press and release keeps Start from opening.
    private static void ReleaseCopilotModifiers(bool releaseWin, bool releaseShift)
    {
        if (releaseShift) InjectedKeys.SendUp(InjectedKeys.LShift);
        if (!releaseWin) return;
        InjectedKeys.SendTap(InjectedKeys.Unassigned);
        InjectedKeys.SendUp(InjectedKeys.LWin);
    }

    // Raised on the UI thread, or on a timer thread when a single Right Alt tap expires.
    private void OnAction(DictationAction action, KeySource source)
    {
        if (!_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(() => OnAction(action, source));
            return;
        }
        if (_owner is { } owner && owner != source)
        {
            Write($"do    {action} from {source} ignored, {owner} owns this dictation");
            return;
        }
        if (Paused && action == DictationAction.StartCapture && _owner is null)
        {
            Write($"do    {action} from {source} ignored, Murmur is paused");
            return;
        }
        Write($"do    {action} ({source})");
        if (action == DictationAction.StartCapture)
        {
            _owner = source;
            _sink.Next = source == KeySource.Copilot ? DictationTarget.Bot : DictationTarget.Cursor;
            if (Settings.KeepMicWarm && !_mic.IsRunning) _mic.Start(); // Reopen after the device failed or changed.
        }
        _controller.Handle(action);
        ReleaseOwnerIfDone();
    }

    private void ReleaseOwnerIfDone()
    {
        if (_controller.Status == DictationStatus.Idle && _altMachine.State == DictationState.Idle) _owner = null;
    }

    private RecordingPill.ListenMode ListeningMode => _owner == KeySource.Copilot ? RecordingPill.ListenMode.Bot
        : _altMachine.State == DictationState.HandsFree ? RecordingPill.ListenMode.HandsFree
        : RecordingPill.ListenMode.Cursor;

    private void OnAltState(DictationState state)
    {
        switch (state)
        {
            // A single tap also opens the mic, but the pill waits until it's clearly a dictation.
            case DictationState.PushToTalk or DictationState.HandsFree when _controller.Status == DictationStatus.Listening:
                ShowListeningCard();
                Write($"pill  {state}");
                break;
            case DictationState.Idle:
                ReleaseOwnerIfDone();
                break;
        }
        UpdateGlyph();
        CancelAvailableChanged?.Invoke(CancelAvailable);
    }

    private void OnStatus(DictationStatus status)
    {
        _lastActive = _clock.Elapsed;
        switch (status)
        {
            case DictationStatus.Listening:
                // Read the app and the text around the cursor while the user talks, so it's ready by the end.
                _context = Settings.AppStyle || Settings.ScreenContext
                    ? GrokVoice.Context.AppAwareness.CaptureAsync(_sink.Keyboard.Target, Settings.ScreenContext, forBot: _owner == KeySource.Copilot)
                    : null;
                if (_diagnostics is { } diag) _mic.BeginRaw(Path.Combine(diag, "device.wav"));
                _sessionStart = _clock.Elapsed;
                _sessionPeak = 0;
                _error = false;
                if (_owner == KeySource.Copilot || _altMachine.State is DictationState.PushToTalk or DictationState.HandsFree)
                    ShowListeningCard();
                if (_owner == KeySource.Copilot) ShowAvatarCard();
                break;
            case DictationStatus.Transcribing:
                _mic.EndRaw();
                _pill.ShowTranscribing();
                break;
            case DictationStatus.Idle:
                _mic.EndRaw();
                _pill.HideUnlessShowingError();
                ReleaseOwnerIfDone();
                break;
        }
        Write($"state {status} at {(_clock.Elapsed - _sessionStart).TotalMilliseconds:0} ms");
        UpdateGlyph();
        StatusChanged?.Invoke(status);
        CancelAvailableChanged?.Invoke(CancelAvailable);
    }

    private void OnInserted(string text)
    {
        LastText = _sink.LastDelivered is { Length: > 0 } delivered ? delivered : text;
        SaveResult(text, LastText);
        if (_sink.Current == DictationTarget.Bot)
        {
            _pill.ShowSent(Bot.BotName ?? "your bot", LastText, _sink.Keyboard.Target);
            ShowAvatarCard();
            _awaitingBot = true;
            UpdateGlyph();
        }
        Write($"{(_sink.Current == DictationTarget.Bot ? "sent" : "typed")} {text.Length} chars, {(_clock.Elapsed - _sessionStart).TotalMilliseconds:0} ms after start");
        Inserted?.Invoke(text);
    }

    private void OnBotReply(string botName, string reply)
    {
        Write($"bot   {botName} replied ({reply.Length} chars)");
        _awaitingBot = false;
        // Never cover a dictation in progress; the reply is in Grok Bot either way.
        if (_controller.Status != DictationStatus.Idle)
        {
            UpdateGlyph();
            return;
        }
        _pill.ShowReply(botName, reply, _sink.Keyboard.Target);
        ShowAvatarCard();
        UpdateGlyph();
    }

    private string? BotAvatarClip => Settings.AvatarClip(Settings.BotId) is { Length: > 0 } path ? path : null;

    private void OnFailed(string message)
    {
        Write($"error {message}");
        _pill.ShowError(message, _sink.Keyboard.Target);
        ReleaseOwnerIfDone();
        _awaitingBot = false;
        _error = true;
        _errorTimer.Stop();
        _errorTimer.Start();
        UpdateGlyph();
        Failed?.Invoke(message);
    }

    private void OnLevel(float peak)
    {
        // Capture thread, every 50 ms; skip if the UI hasn't caught up.
        if (peak > _sessionPeak) _sessionPeak = peak;
        if (Interlocked.Exchange(ref _levelPending, 1) == 1) return;
        _ui.TryEnqueue(() =>
        {
            Volatile.Write(ref _levelPending, 0);
            _pill.SetLevel(peak);
            _glyph.SetLevel(peak);
        });
    }

    public void Dispose()
    {
        _hook.Dispose();
        _alt.Dispose();
        _copilot.Dispose();
        _altMachine.Dispose();
        _controller.Dispose();
        _mic.Dispose();
        _pill.Hide();
        _glyph.Dispose();
    }
}
