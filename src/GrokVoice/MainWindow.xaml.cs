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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 760));

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
        _host.SettingsRequested += Present;
        _host.QuitRequested += () => Quit_Click(this, new RoutedEventArgs());
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
        RightAltSwitch.IsOn = _host.RightAltEnabled;
        PolishSwitch.IsOn = _host.Settings.PolishEnabled;
        RestingSwitch.IsOn = _host.RestingPill;
        AppStyleSwitch.IsOn = _host.Settings.AppStyle;
        ScreenSwitch.IsOn = _host.Settings.ScreenContext;
        ShowMicrophones();
        ShowUsage();
        _host.Usage.Changed += () => DispatcherQueue.TryEnqueue(ShowUsage);
        _host.Updates.StatusChanged += () => DispatcherQueue.TryEnqueue(ShowUpdateStatus);
        ShowUpdateStatus();
        ShowDictionary();
        _host.Words.Changed += () => DispatcherQueue.TryEnqueue(ShowDictionary);
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
        if (_host.SetupNeeded is not null) Navigate("Accounts");
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) ShowPage(tag);
    }

    private void Navigate(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void ShowPage(string tag)
    {
        // Nav fires before InitializeComponent has made every page.
        if (GeneralPage is null || DictionaryPage is null) return;
        foreach (var (name, page) in new (string, UIElement)[]
        {
            ("General", GeneralPage), ("Dictation", DictationPage), ("Jeeves", JeevesPage),
            ("Usage", UsagePage), ("Accounts", AccountsPage), ("About", AboutPage),
        })
            page.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPage.Visibility = tag == "Dictionary" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.Visibility = tag == "Dictionary" ? Visibility.Collapsed : Visibility.Visible;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
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
        KeyBar.Title = "The Copilot key isn't set to Murmur";
        KeyBar.Message = "Open Copilot key below and choose Murmur. Right Alt works either way.";
        CopilotKeyCard.Description = ours
            ? "Set to Murmur. Hold to talk to Jeeves, tap to open Grok Bot."
            : "Choose Murmur in Windows Settings.";
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

    private async Task TestAsync()
    {
        if (_host.Keys.Get() is not { Length: > 0 } key) return;
        TestBar.IsOpen = false;
        TestRing.IsActive = true;
        var error = await _host.TestKeyAsync(key);
        TestRing.IsActive = false;

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

    private void PolishSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.PolishEnabled = PolishSwitch.IsOn;
    }

    private string _dictionaryFilter = "all";

    private void ShowDictionary()
    {
        var d = _host.Words;
        var query = DictionarySearch.Text.Trim();
        var rows = d.Entries
            .Where(e => _dictionaryFilter switch
            {
                "words" => e.Replacement is null,
                "replacements" => e.Replacement is not null && !e.IsSnippet,
                "snippets" => e.IsSnippet,
                _ => true,
            })
            .Where(e => query.Length == 0
                || e.Phrase.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (e.Replacement?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(e => new DictionaryRow(e))
            .ToList();
        DictionaryList.ItemsSource = rows;
        FilterAll.Text = $"All ({d.Entries.Count})";
        FilterWords.Text = $"Words ({d.Words})";
        FilterReplacements.Text = $"Replacements ({d.Replacements})";
        FilterSnippets.Text = $"Snippets ({d.Snippets})";
        DictionaryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DictionaryEmptyText.Text = d.Entries.Count == 0
            ? "Add names and words Murmur should spell your way, or phrases it should swap for something else."
            : "Nothing matches.";
        WisprImportButton.IsEnabled = GrokVoice.Words.WisprImport.DatabasePath is not null;
    }

    private void DictionarySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ShowDictionary();

    private void DictionaryFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _dictionaryFilter = sender.SelectedItem?.Tag as string ?? "all";
        if (DictionaryList is not null) ShowDictionary();
    }

    private async void AddEntry_Click(object sender, RoutedEventArgs e) => await EditEntryAsync(null);

    private async void DictionaryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DictionaryRow row) await EditEntryAsync(row.Entry);
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictionaryRow row) return;
        RemoveWithUndo(row.Entry);
    }

    private GrokVoice.Words.DictionaryEntry? _removed;

    /// <summary>Removes an entry and offers Undo, since a remove is one click.</summary>
    private void RemoveWithUndo(GrokVoice.Words.DictionaryEntry entry)
    {
        _host.Words.Remove(entry.Phrase);
        _host.Write($"dict  removed \"{entry.Phrase}\"");
        _removed = entry;
        var undo = new Button { Content = "Undo" };
        undo.Click += (_, _) =>
        {
            if (_removed is { } back) _host.Words.Merge([back]);
            _removed = null;
            DictionaryBar.IsOpen = false;
        };
        DictionaryBar.ActionButton = undo;
        ShowDictionaryBar(InfoBarSeverity.Informational, "Removed", $"\"{entry.Phrase}\"");
    }

    private async void ClearDictionary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Remove everything?",
            Content = $"All {_host.Words.Entries.Count} entries will be removed. This can't be undone.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _host.Words.Clear();
        _host.Write("dict  cleared");
    }

    /// <summary>The add and edit dialog: what you say, and optionally what to write instead.</summary>
    private async Task EditEntryAsync(GrokVoice.Words.DictionaryEntry? entry)
    {
        var phrase = new TextBox { Header = "When I say", Text = entry?.Phrase ?? "", PlaceholderText = "A word, name or phrase" };
        var replacement = new TextBox
        {
            Header = "Write instead (optional)",
            Text = entry?.Replacement ?? "",
            PlaceholderText = "Leave empty to just fix the spelling",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 160,
        };
        var snippet = new CheckBox
        {
            Content = "Snippet (a longer block of text, like an address)",
            IsChecked = entry?.IsSnippet ?? false,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = entry is null ? "Add to dictionary" : "Edit entry",
            Content = new StackPanel { Spacing = 12, MinWidth = 400, Children = { phrase, replacement, snippet } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = phrase.Text.Trim().Length > 0,
        };
        if (entry is not null) dialog.SecondaryButtonText = "Remove";
        phrase.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = phrase.Text.Trim().Length > 0;
        void SnippetState() => snippet.IsEnabled = replacement.Text.Trim().Length > 0;
        replacement.TextChanged += (_, _) => SnippetState();
        SnippetState();
        dialog.Opened += (_, _) => phrase.Focus(FocusState.Programmatic);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && entry is not null)
        {
            RemoveWithUndo(entry);
            return;
        }
        if (result != ContentDialogResult.Primary) return;
        var updated = new GrokVoice.Words.DictionaryEntry(phrase.Text.Trim(),
            replacement.Text.Trim() is { Length: > 0 } r ? r : null, snippet.IsChecked == true);
        if (entry is null) _host.Words.Merge([updated]);
        else _host.Words.Update(entry.Phrase, updated);
        _host.Write($"dict  {(entry is null ? "added" : "edited")} \"{updated.Phrase}\"");
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
            $"{entries.Count} entries read, {added} new. Murmur now uses them for every dictation.");
    }

    private void ShowDictionaryBar(InfoBarSeverity severity, string title, string message)
    {
        if (title != "Removed") DictionaryBar.ActionButton = null;
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
            ("This month", usage.MonthToDate),
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
        string[] headers = ["", "Dictations", "Audio", "Cost"];
        for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], header: true);
        for (var r = 0; r < rows.Length; r++)
        {
            var t = rows[r].T;
            UsageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(r + 1, 0, rows[r].Name);
            Cell(r + 1, 1, t.Dictations.ToString("N0"));
            Cell(r + 1, 2, FormatAudio(t.AudioSeconds));
            Cell(r + 1, 3, FormatCost(t.Cost), strong: true);
        }
        var month = usage.MonthToDate;
        UsageNote.Text = $"This month: ElevenLabs {FormatCost(month.ScribeCost)}, Grok speech to text {FormatCost(month.GrokTranscribeCost)}, "
            + $"clean-up {FormatCost(month.CleanUpCost)}. Jeeves runs on your Grok subscription and isn't counted here.";
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
                ErrorBar.Message = "Windows has Murmur turned off as a startup app. Turn it on in Settings > Apps > Startup.";
                ErrorBar.IsOpen = true;
            }
        }
        else
        {
            task.Disable();
        }
        await LoadStartupStateAsync();
    }

    private void ShowUpdateStatus()
    {
        var version = $"Version {GrokVoice.Platform.Updater.Current.ToString(3)}";
        var status = _host.Updates.Status;
        UpdatesCard.Description = status.Length > 0 && !status.StartsWith("Up to date")
            ? $"{version}. {status}"
            : $"{version}. Updates install on their own.";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try { await _host.Updates.CheckAsync(manual: true); }
        finally { CheckUpdatesButton.IsEnabled = true; ShowUpdateStatus(); }
    }

    private async void CopilotKeySettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-textinput"));

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

/// <summary>One row of the dictionary list.</summary>
public sealed class DictionaryRow
{
    internal DictionaryRow(GrokVoice.Words.DictionaryEntry entry) => Entry = entry;

    internal GrokVoice.Words.DictionaryEntry Entry { get; }
    public string Phrase => Entry.Phrase;
    public string Detail => Entry.Replacement is { } r ? "→ " + r.ReplaceLineEndings(" ") : "";
    public Visibility DetailVisibility => Entry.Replacement is null ? Visibility.Collapsed : Visibility.Visible;
    public string Kind => Entry.IsSnippet ? "Snippet" : Entry.Replacement is null ? "Word" : "Replacement";
    public string RemoveName => $"Remove {Entry.Phrase}";

    /// <summary>What screen readers and UI Automation read for the row.</summary>
    public override string ToString() => Entry.Replacement is { } r ? $"{Entry.Phrase}, writes {r}" : Entry.Phrase;
}
