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
        Bot = new BotSender(this);
        var keyboard = new KeyboardTextSink { IsOwnWindow = h => h == _pill.Handle || h == MainWindowHandle };
        Words = new GrokVoice.Words.WordDictionary();
        Polisher = new GrokVoice.Words.TextPolisher(() => Keys.Get(), Words, () => Settings.PolishEnabled, Usage);
        _sink = new RoutingTextSink(keyboard, Bot, Polisher.PolishAsync);

        // Before the controller, which opens the microphone straight away when it's kept warm.
        _mic.Faulted += m => _ui.TryEnqueue(() => OnFailed(m));
        _mic.LevelChanged += OnLevel;

        _controller = new DictationController(_mic, _sink, CreateTranscriber, Settings.KeepMicWarm);
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
        Bot.NoReply += name => Write($"bot   no reply from {name} within 5 minutes");
        _pill.Clicked += () =>
        {
            if (_controller.Status == DictationStatus.Idle) OpenGrokBot();
        };
        _pill.Resting = Settings.RestingPill;

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

    /// <summary>Whether the thin resting pill stays on screen while idle.</summary>
    public bool RestingPill
    {
        get => Settings.RestingPill;
        set
        {
            Settings.RestingPill = value;
            _pill.Resting = value;
        }
    }

    public AppSettings Settings { get; } = new();
    public ApiKeyStore Keys { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public DictationStatus Status => _controller.Status;
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
    public string? SetupNeeded => string.IsNullOrWhiteSpace(Keys.Get())
        ? "Add your xAI API key in Grok Voice's settings to start dictating."
        : null;

    /// <summary>Whether Right Alt dictates. Off lets Right Alt through untouched (for Wispr Flow, say).</summary>
    public bool RightAltEnabled
    {
        get => Settings.RightAltEnabled;
        set
        {
            Settings.RightAltEnabled = value;
            _hook.RightAltEnabled = value;
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
        return new XaiRestTranscriber(new XaiSttOptions { ApiKey = Keys.Get()!, Language = Settings.Language }, Words.Keyterms(),
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
                _pill.ShowListening(_sink.Keyboard.Target, ListeningMode);
                Write($"pill  {state}");
                break;
            case DictationState.Idle:
                ReleaseOwnerIfDone();
                break;
        }
        CancelAvailableChanged?.Invoke(CancelAvailable);
    }

    private void OnStatus(DictationStatus status)
    {
        switch (status)
        {
            case DictationStatus.Listening:
                _sessionStart = _clock.Elapsed;
                _sessionPeak = 0;
                if (_owner == KeySource.Copilot || _altMachine.State is DictationState.PushToTalk or DictationState.HandsFree)
                    _pill.ShowListening(_sink.Keyboard.Target, ListeningMode);
                if (_owner == KeySource.Copilot) _pill.ShowAvatar(BotAvatarClip);
                break;
            case DictationStatus.Transcribing:
                _pill.ShowTranscribing();
                break;
            case DictationStatus.Idle:
                _pill.HideUnlessShowingError();
                ReleaseOwnerIfDone();
                break;
        }
        Write($"state {status} at {(_clock.Elapsed - _sessionStart).TotalMilliseconds:0} ms");
        StatusChanged?.Invoke(status);
        CancelAvailableChanged?.Invoke(CancelAvailable);
    }

    private void OnInserted(string text)
    {
        LastText = _sink.LastDelivered is { Length: > 0 } delivered ? delivered : text;
        SaveResult(text, LastText);
        if (_sink.Current == DictationTarget.Bot)
        {
            _pill.ShowInfo($"Sent to {Bot.BotName ?? "your bot"}", _sink.Keyboard.Target);
            _pill.ShowAvatar(BotAvatarClip);
        }
        Write($"{(_sink.Current == DictationTarget.Bot ? "sent" : "typed")} {text.Length} chars, {(_clock.Elapsed - _sessionStart).TotalMilliseconds:0} ms after start");
        Inserted?.Invoke(text);
    }

    private void OnBotReply(string botName, string reply)
    {
        Write($"bot   {botName} replied ({reply.Length} chars)");
        // Never cover a dictation in progress; the reply is in Grok Bot either way.
        if (_controller.Status != DictationStatus.Idle) return;
        _pill.ShowReply(botName, reply, _sink.Keyboard.Target);
        _pill.ShowAvatar(BotAvatarClip);
    }

    private string? BotAvatarClip => Settings.AvatarClip(Settings.BotId) is { Length: > 0 } path ? path : null;

    private void OnFailed(string message)
    {
        Write($"error {message}");
        _pill.ShowError(message, _sink.Keyboard.Target);
        ReleaseOwnerIfDone();
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
    }
}
