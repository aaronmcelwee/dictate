using System.Web;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace GrokVoice;

public partial class App : Application
{
    private DictationHost? _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // Grok Voice lives in the background with no visible window; only Quit ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _host = new DictationHost(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow(_host);

        var current = AppInstance.GetCurrent();
        current.Activated += (_, a) => _window.DispatcherQueue.TryEnqueue(() => HandleActivation(a, redirected: true));
        HandleActivation(current.GetActivatedEventArgs(), redirected: false);
        _ = EnableStartupOnceAsync();
    }

    /// <summary>
    /// Starts with Windows by default: switched on once, the first time this version runs. If the user
    /// turns it off later (here or in Settings > Apps > Startup), that choice stands.
    /// </summary>
    private async Task EnableStartupOnceAsync()
    {
        if (_host!.Settings.StartupDefaulted) return;
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync("GrokVoiceStartup");
            if (task.State == Windows.ApplicationModel.StartupTaskState.Disabled)
            {
                var state = await task.RequestEnableAsync();
                _host.Write($"start with Windows: {state}");
            }
            _host.Settings.StartupDefaulted = true;
        }
        catch (Exception e)
        {
            _host.Write($"start with Windows failed: {e.Message}");
        }
    }

    private void HandleActivation(AppActivationArguments args, bool redirected)
    {
        switch (args.Kind)
        {
            // A cold start from the Copilot key arrives as ProtocolForResults, a running instance gets Protocol.
            case ExtendedActivationKind.Protocol or ExtendedActivationKind.ProtocolForResults
                when args.Data is IProtocolActivatedEventArgs p:
                var state = HttpUtility.ParseQueryString(p.Uri.Query)["state"];
                if (CopilotKeyFastPath.FromUriState(state) is { } key)
                    _host!.OnProviderKey(key, redirected ? "uri" : "uri (cold)");
                else
                    _host!.Write($"uri   unknown state '{state}'");
                break;

            // Signed in with "Start with Windows" on: stay in the background.
            case ExtendedActivationKind.StartupTask:
                _host!.Write("start at sign-in");
                break;

            // Windows restarted Murmur after an in-place update: stay in the background.
            case ExtendedActivationKind.Launch when !redirected && Environment.CommandLine.Contains("--updated"):
                _host!.ShowUpdated();
                break;

            default:
                _window!.Present();
                break;
        }
    }
}
