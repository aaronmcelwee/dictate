using Dictate.Core;
using Dictate.Core.Dictation;
using GrokVoice.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GrokVoice;

/// <summary>Settings and status. Closing it only hides it: the app keeps listening for the Copilot key.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly (string Name, string Code)[] Languages =
    {
        ("Auto-detect", ""), ("English", "en"), ("Spanish", "es"), ("French", "fr"), ("German", "de"),
        ("Italian", "it"), ("Portuguese", "pt"), ("Dutch", "nl"), ("Japanese", "ja"), ("Korean", "ko"),
        ("Chinese", "zh"), ("Hindi", "hi"),
    };

    private const string StartupTaskId = "GrokVoiceStartup";

    private readonly DictationHost _host;
    private readonly CopilotKeyFastPath _fastPath;
    private readonly EscapeHotKey _escape;
    private bool _loading = true;

    internal MainWindow(DictationHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 900));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _host.MainWindowHandle = hwnd;
        _fastPath = new CopilotKeyFastPath(hwnd, e => _host.OnProviderKey(e, "fast path"));
        _escape = new EscapeHotKey(hwnd, _host.Cancel);

        // Hide instead of closing: the window carries the Copilot key fast path.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) ShowKeyRegistration();
        };

        _host.StatusChanged += OnStatus;
        // Escape belongs to Grok Voice only during hands-free dictation.
        _host.CancelAvailableChanged += available => _escape.Enabled = available;
        _host.Inserted += text => LastText.Text = _host.Polisher.LastRaw is { Length: > 0 } raw && raw != text
            ? $"{text}\n\nAs heard, before clean-up:\n{raw}" : text;
        _host.Failed += message =>
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        };

        LogList.ItemsSource = _host.Log;
        foreach (var (name, code) in Languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == _host.Settings.Language));
        EngineBox.SelectedIndex = _host.Settings.SpeechEngine == "elevenlabs" ? 1 : 0;
        ShowElevenKeyState();
        WarmMicSwitch.IsOn = _host.KeepMicWarm;
        RightAltSwitch.IsOn = _host.RightAltEnabled;
        PolishSwitch.IsOn = _host.Settings.PolishEnabled;
        RestingSwitch.IsOn = _host.RestingPill;
        AppStyleSwitch.IsOn = _host.Settings.AppStyle;
        ScreenSwitch.IsOn = _host.Settings.ScreenContext;
        ShowMicrophones();
        ShowUsage();
        _host.Usage.Changed += () => DispatcherQueue.TryEnqueue(ShowUsage);
        ShowDictionary();
        ShowBotState();
        ShowApiKeyState();
        ShowKeyRegistration();
        OnStatus(_host.Status);
        _ = LoadStartupStateAsync();
        _loading = false;
        _ = LoadBotsAsync();
    }

    /// <summary>Shows the window and brings it to the front.</summary>
    public void Present()
    {
        AppWindow.Show();
        Activate();
        if (string.IsNullOrWhiteSpace(_host.Keys.Get()))
            ApiKeyBox.Focus(FocusState.Programmatic);
    }

    private void OnStatus(DictationStatus status)
    {
        StatusText.Text = status switch
        {
            DictationStatus.Listening => "Listening…",
            DictationStatus.Transcribing => "Transcribing…",
            _ when _host.SetupNeeded is not null => "Add your xAI API key to start",
            _ => "Ready",
        };
    }

    private void ShowApiKeyState()
    {
        var saved = !string.IsNullOrWhiteSpace(_host.Keys.Get());
        ApiKeyState.Text = saved ? "Saved" : "Not set";
        RemoveKeyButton.IsEnabled = saved;
        TestKeyButton.IsEnabled = saved;
        ApiKeyBox.PlaceholderText = saved ? "Saved. Paste a new key to replace it." : "xai-…";
        OnStatus(_host.Status);
    }

    private void ShowKeyRegistration()
    {
        string aumid;
        try
        {
            aumid = $"{Package.Current.Id.FamilyName}!App";
        }
        catch (InvalidOperationException)
        {
            KeyBar.Severity = InfoBarSeverity.Error;
            KeyBar.Title = "Not installed as a package";
            KeyBar.Message = "Run the installed MSIX; Windows only gives the Copilot key to packaged, signed apps.";
            KeyBar.IsOpen = true;
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\BrandedKey");
        var choice = key?.GetValue("BrandedKeyChoiceType") as string;
        var selected = key?.GetValue("AppAumid") as string;
        var ours = choice == "App" && string.Equals(selected, aumid, StringComparison.OrdinalIgnoreCase);

        KeyBar.IsOpen = !ours;
        KeyBar.Severity = InfoBarSeverity.Warning;
        KeyBar.Title = "The Copilot key isn't set to Grok Voice";
        KeyBar.Message = "Open Customize Copilot key below and choose Grok Voice. Right Alt works either way.";
        CopilotKeyCard.Description = ours
            ? "Grok Voice owns the Copilot key."
            : "Choose Grok Voice under Personalization > Text input.";
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e) => SaveKey();

    private void ApiKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SaveKey();
    }

    private async void SaveKey()
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0) return;
        _host.Keys.Set(key);
        ApiKeyBox.Password = "";
        ShowApiKeyState();
        await TestAsync();
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e) => await TestAsync();

    private async Task TestAsync()
    {
        if (_host.Keys.Get() is not { Length: > 0 } key) return;
        TestBar.IsOpen = false;
        TestRing.IsActive = true;
        TestKeyButton.IsEnabled = false;
        var error = await _host.TestKeyAsync(key);
        TestRing.IsActive = false;
        TestKeyButton.IsEnabled = true;

        TestBar.Severity = error is null ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        TestBar.Title = error is null ? "Grok is connected" : "Grok didn't accept the connection";
        TestBar.Message = error ?? "Hold Right Alt and start talking.";
        TestBar.IsOpen = true;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        _host.Keys.Clear();
        TestBar.IsOpen = false;
        ShowApiKeyState();
    }

    private CancellationTokenSource? _signIn;

    private void ShowBotState()
    {
        var signedIn = _host.Bot.Client.IsSignedIn;
        BotState.Text = _signIn is not null ? "Finish signing in in your browser…"
            : !signedIn ? "Not signed in"
            : _host.Bot.BotName ?? "Signed in";
        BotSignInButton.Content = _signIn is not null ? "Cancel" : signedIn ? "Sign out" : "Sign in";
        BotSignInButton.Style = !signedIn && _signIn is null ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        BotBox.IsEnabled = signedIn;
        ShowAvatarClip();
    }

    private void ShowBotError(string title, string message)
    {
        BotBar.Severity = InfoBarSeverity.Error;
        BotBar.Title = title;
        BotBar.Message = message;
        BotBar.IsOpen = true;
    }

    private async void BotSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_signIn is { } running)
        {
            running.Cancel();
            return;
        }
        if (_host.Bot.Client.IsSignedIn)
        {
            _host.Bot.Client.SignOut();
            _host.Bot.Choose(null);
            BotBox.ItemsSource = null;
            ShowBotState();
            return;
        }

        using var cts = new CancellationTokenSource();
        _signIn = cts;
        BotBar.IsOpen = false;
        ShowBotState();
        try
        {
            await _host.Bot.Client.SignInAsync(cts.Token);
            _host.Write("bot   signed in to Grok Bot");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowBotError("Sign-in didn't finish", ex.Message);
        }
        finally
        {
            _signIn = null;
            ShowBotState();
        }
        if (_host.Bot.Client.IsSignedIn) await LoadBotsAsync();
    }

    private async void BotRefresh_Click(object sender, RoutedEventArgs e) => await LoadBotsAsync();

    private async Task LoadBotsAsync()
    {
        if (!_host.Bot.Client.IsSignedIn) return;
        BotRing.IsActive = true;
        try
        {
            var bots = await _host.Bot.Client.ListBotsAsync(CancellationToken.None);
            _loading = true;
            BotBox.ItemsSource = bots;
            var current = bots.FirstOrDefault(b => b.Id == _host.Settings.BotId);
            current ??= GrokVoice.Bot.BotSender.DefaultBot(bots);
            BotBox.SelectedItem = current;
            _host.Bot.Choose(current);
            _loading = false;
            _host.Write($"bot   {bots.Count} bot(s) in the account");
        }
        catch (Exception ex)
        {
            ShowBotError("Couldn't load your bots", ex.Message);
        }
        finally
        {
            _loading = false;
            BotRing.IsActive = false;
            ShowBotState();
        }
    }

    private void BotBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BotBox.SelectedItem is not GrokVoice.Bot.GrokBot bot) return;
        _host.Bot.Choose(bot);
        ShowBotState();
    }

    private void ShowAvatarClip()
    {
        var id = _host.Settings.BotId;
        var path = _host.Settings.AvatarClip(id);
        AvatarCard.IsEnabled = id.Length > 0;
        AvatarName.Text = path.Length > 0 ? Path.GetFileName(path) : "None";
        AvatarRemoveButton.Visibility = path.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AvatarChoose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
        };
        foreach (var ext in new[] { ".mp4", ".webm", ".mov" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _host.Settings.SetAvatarClip(_host.Settings.BotId, file.Path);
        _host.Write($"bot   character clip set to {file.Name}");
        ShowAvatarClip();
    }

    private void AvatarRemove_Click(object sender, RoutedEventArgs e)
    {
        _host.Settings.SetAvatarClip(_host.Settings.BotId, null);
        ShowAvatarClip();
    }

    private void PolishSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.PolishEnabled = PolishSwitch.IsOn;
    }

    private void ShowDictionary()
    {
        var d = _host.Words;
        DictionaryCard.Description = d.Entries.Count == 0
            ? "Your words, replacements and snippets. Import them from Wispr Flow to start."
            : $"{d.Words} words, {d.Replacements} replacements and {d.Snippets} snippets.";
        WisprImportButton.IsEnabled = GrokVoice.Words.WisprImport.DatabasePath is not null;
    }

    private async void WisprImport_Click(object sender, RoutedEventArgs e)
    {
        if (GrokVoice.Words.WisprImport.DatabasePath is not { } path) return;
        WisprImportButton.IsEnabled = false;
        try
        {
            var entries = await Task.Run(() => GrokVoice.Words.WisprImport.FromDatabase(path));
            ShowImported(entries);
        }
        catch (Exception ex)
        {
            ShowDictionaryBar(InfoBarSeverity.Error, "Couldn't read Wispr Flow's dictionary", ex.Message);
        }
        finally
        {
            ShowDictionary();
        }
    }

    private async void DictionaryFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".sqlite");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var entries = await Task.Run(() => file.Path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                ? GrokVoice.Words.WisprImport.FromDatabase(file.Path)
                : GrokVoice.Words.WisprImport.FromJson(file.Path));
            ShowImported(entries);
        }
        catch (Exception ex)
        {
            ShowDictionaryBar(InfoBarSeverity.Error, "Couldn't import that file", ex.Message);
        }
        ShowDictionary();
    }

    private void ShowImported(List<GrokVoice.Words.DictionaryEntry> entries)
    {
        var added = _host.Words.Merge(entries);
        _host.Write($"dict  imported {entries.Count} entries, {added} new");
        ShowDictionaryBar(InfoBarSeverity.Success, "Imported",
            $"{entries.Count} entries read, {added} new. Grok Voice now uses them for every dictation.");
    }

    private void ShowDictionaryBar(InfoBarSeverity severity, string title, string message)
    {
        DictionaryBar.Severity = severity;
        DictionaryBar.Title = title;
        DictionaryBar.Message = message;
        DictionaryBar.IsOpen = true;
    }

    private void RightAltSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.RightAltEnabled = RightAltSwitch.IsOn;
    }

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EngineBox.SelectedItem is not ComboBoxItem { Tag: string engine }) return;
        _host.Settings.SpeechEngine = engine;
        _host.Write($"stt   speech-to-text: {engine}");
        ShowElevenKeyState();
    }

    private void ShowElevenKeyState()
    {
        var saved = !string.IsNullOrWhiteSpace(_host.ElevenKeys.Get());
        ElevenKeyState.Text = saved ? "Saved"
            : _host.Settings.SpeechEngine == "elevenlabs" ? "Not set: using Grok until a key is saved" : "Not set";
        RemoveElevenKeyButton.IsEnabled = saved;
        ElevenKeyBox.PlaceholderText = saved ? "Saved. Paste a new key to replace it." : "sk_…";
    }

    private void SaveElevenKey_Click(object sender, RoutedEventArgs e) => SaveElevenKey();

    private void ElevenKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SaveElevenKey();
    }

    private void SaveElevenKey()
    {
        var key = ElevenKeyBox.Password.Trim();
        if (key.Length == 0) return;
        _host.ElevenKeys.Set(key);
        ElevenKeyBox.Password = "";
        _host.Write("stt   ElevenLabs key saved");
        // Saving a key means "use it": switch speech-to-text over (the picker can switch back).
        _host.Settings.SpeechEngine = "elevenlabs";
        _loading = true;
        EngineBox.SelectedIndex = 1;
        _loading = false;
        _host.Write("stt   speech-to-text: elevenlabs");
        ShowElevenKeyState();
        _host.OnElevenKeySaved();
    }

    private void RemoveElevenKey_Click(object sender, RoutedEventArgs e)
    {
        _host.ElevenKeys.Clear();
        ShowElevenKeyState();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        _host.Settings.Language = code;
    }

    private void ShowMicrophones()
    {
        MicBox.Items.Clear();
        MicBox.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
        foreach (var (id, name) in GrokVoice.Platform.MicrophoneSource.Devices())
            MicBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        var current = _host.MicrophoneId;
        MicBox.SelectedIndex = Math.Max(0, MicBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == current));
    }

    private void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        if (id != _host.MicrophoneId) _host.MicrophoneId = id;
    }

    private void AppStyleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.Settings.AppStyle = AppStyleSwitch.IsOn;
    }

    private void ScreenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.Settings.ScreenContext = ScreenSwitch.IsOn;
    }

    private void RestingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.RestingPill = RestingSwitch.IsOn;
    }

    private void ShowUsage()
    {
        var usage = _host.Usage;
        (string Name, GrokVoice.Usage.UsageTotals T)[] rows =
        [
            ("Today", usage.Today),
            ("This week", usage.ThisWeek),
            ("Month to date", usage.MonthToDate),
            ("Year to date", usage.YearToDate),
            ("All time", usage.AllTime),
        ];
        UsageGrid.Children.Clear();
        UsageGrid.RowDefinitions.Clear();
        void Cell(int row, int column, string text, bool header = false, bool strong = false)
        {
            var block = new TextBlock
            {
                Text = text,
                HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Style = (Style)Application.Current.Resources[header ? "CaptionTextBlockStyle" : strong ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"],
            };
            if (header) block.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            UsageGrid.Children.Add(block);
        }
        UsageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] headers = ["", "Dictations", "Audio", "Clean-up tokens", "Cost (USD)"];
        for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], header: true);
        for (var r = 0; r < rows.Length; r++)
        {
            var t = rows[r].T;
            UsageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(r + 1, 0, rows[r].Name);
            Cell(r + 1, 1, t.Dictations.ToString("N0"));
            Cell(r + 1, 2, FormatAudio(t.AudioSeconds));
            Cell(r + 1, 3, t.Tokens.ToString("N0"));
            Cell(r + 1, 4, FormatCost(t.Cost), strong: true);
        }
        var all = usage.AllTime;
        UsageNote.Text = $"Transcription {FormatCost(all.TranscribeCost)} and clean-up {FormatCost(all.CleanUpCost)} all time "
            + $"({all.InputTokens:N0} tokens in, {all.OutputTokens:N0} out). Counted from this version on, on this PC. "
            + "Costs use xAI's own figure where the response gives one, else list prices. "
            + "Messages to Grok Bot go through your Grok subscription and aren't billed here.";
    }

    private static string FormatAudio(double seconds) =>
        seconds < 60 ? $"{seconds:0} s" : seconds < 3600 ? $"{seconds / 60:0.0} min" : $"{seconds / 3600:0.0} h";

    private static string FormatCost(decimal usd) =>
        usd == 0 ? "$0.00" : usd < 0.01m ? $"${usd:0.0000}" : $"${usd:0.00}";

    private async void UsageExport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "grok-voice-usage" };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is { } file)
            await Windows.Storage.FileIO.WriteTextAsync(file, _host.Usage.ToCsv());
    }

    private void WarmMicSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.KeepMicWarm = WarmMicSwitch.IsOn;
    }

    private async Task LoadStartupStateAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            _loading = true;
            StartupSwitch.IsOn = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupSwitch.IsEnabled = task.State is not (StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy);
        }
        catch (Exception)
        {
            StartupSwitch.IsEnabled = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var task = await StartupTask.GetAsync(StartupTaskId);
        if (StartupSwitch.IsOn)
        {
            var state = await task.RequestEnableAsync();
            if (state == StartupTaskState.DisabledByUser)
            {
                ErrorBar.Message = "Windows has Grok Voice turned off as a startup app. Turn it on in Settings > Apps > Startup.";
                ErrorBar.IsOpen = true;
            }
        }
        else
        {
            task.Disable();
        }
        await LoadStartupStateAsync();
    }

    private async void CopilotKeySettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-textinput"));

    private async void MicPrivacy_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    private void CopyLast_Click(object sender, RoutedEventArgs e)
    {
        if (_host.LastText is { } text) CopyText(text);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, _host.Log.Reverse()));

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _escape.Dispose();
        _fastPath.Dispose();
        _host.Dispose();
        Application.Current.Exit();
    }
}
