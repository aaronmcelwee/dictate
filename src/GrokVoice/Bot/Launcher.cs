using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace GrokVoice.Bot;

/// <summary>
/// An app from the Start menu, by its AppUserModelID (what shell:AppsFolder opens). ExePath is the
/// program a desktop app's shortcut points to, used to find its open window; null for packaged apps.
/// </summary>
internal sealed record LaunchTarget(string Name, string AppId, string? ExePath = null);

/// <summary>Opens Grok Bot (or another chosen app) when the Copilot key is tapped.</summary>
internal static class Launcher
{
    // Picked on first use when nothing has been chosen yet.
    private const string DefaultAppName = "Grok Bot";

    private static IPropertySet Values => ApplicationData.Current.LocalSettings.Values;

    /// <summary>The app the Copilot key opens, or null when none has been chosen.</summary>
    public static LaunchTarget? Target
    {
        get => Values.TryGetValue("TargetAppId", out var id) && id is string appId && appId.Length > 0
            ? new LaunchTarget(
                Values.TryGetValue("TargetName", out var n) && n is string name ? name : appId,
                appId,
                Values.TryGetValue("TargetExe", out var e) && e is string exe && exe.Length > 0 ? exe : null)
            : null;
        set
        {
            if (value is null)
            {
                Values.Remove("TargetAppId");
                Values.Remove("TargetName");
                Values.Remove("TargetExe");
                return;
            }
            Values["TargetAppId"] = value.AppId;
            Values["TargetName"] = value.Name;
            Values["TargetExe"] = value.ExePath ?? "";
        }
    }

    /// <summary>Whether to put the cursor in the app's text box once it's in front. On by default.</summary>
    public static bool FocusTextBox
    {
        get => !Values.TryGetValue("FocusTextBox", out var v) || v is not bool on || on;
        set => Values["FocusTextBox"] = value;
    }

    // How long to look for the text box: an open window has it already, a starting app shows it late.
    private static readonly TimeSpan FocusPatienceOpen = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FocusPatienceStarting = TimeSpan.FromSeconds(8);

    /// <summary>The chosen app, or the default one (saved as the choice) if it is installed.</summary>
    public static LaunchTarget? TargetOrDefault(IReadOnlyList<LaunchTarget>? startApps = null)
    {
        if (Target is { } target) return target;
        var found = (startApps ?? StartApps.List()).FirstOrDefault(a => a.Name.Contains(DefaultAppName, StringComparison.OrdinalIgnoreCase));
        if (found is null) return null;
        found = WithExePath(found);
        Target = found;
        Log.Write($"no app chosen yet; using {found.Name} ({found.AppId})");
        return found;
    }

    /// <summary>Fills in where a desktop app's program is, so its window can be found without a lookup per press.</summary>
    public static LaunchTarget WithExePath(LaunchTarget target) =>
        target.ExePath is not null || target.AppId.Contains('!') ? target : target with { ExePath = StartApps.TargetPath(target.AppId) };

    /// <summary>Resolves anything the chosen app still lacks, ahead of the first press.</summary>
    public static void Prepare()
    {
        if (TargetOrDefault() is not { } target || target.ExePath is not null || target.AppId.Contains('!')) return;
        var resolved = WithExePath(target);
        if (resolved.ExePath is null) return;
        Target = resolved;
        Log.Write($"{target.Name} runs {resolved.ExePath}");
    }

    /// <summary>
    /// Brings the app's open window to the front, or opens it the way Start does, then (if that's on)
    /// puts the cursor in its text box in the background. Says which it did; the text box result is logged.
    /// </summary>
    public static string Open(LaunchTarget target)
    {
        var how = OpenWindow(target, out var started);
        if (FocusTextBox)
        {
            var clock = Stopwatch.StartNew();
            Task.Run(() =>
            {
                var result = TextBoxFocus.Focus(() => WindowFocus.Find(target), started ? FocusPatienceStarting : FocusPatienceOpen);
                Log.Write($"focus {target.Name}: {result} ({clock.ElapsedMilliseconds} ms after it was {(started ? "started" : "in front")})");
            });
        }
        return how;
    }

    private static string OpenWindow(LaunchTarget target, out bool started)
    {
        started = false;
        var window = WindowFocus.Find(target);
        if (window != IntPtr.Zero)
            return WindowFocus.Bring(window) ? "brought back" : "couldn't bring back (Windows refused)";
        started = true;

        // Lets the app, or the running instance it hands over to, come to the front.
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            Process.Start(new ProcessStartInfo($@"shell:AppsFolder\{target.AppId}") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"Couldn't open {target.Name} ({target.AppId}): {e.Message}", e);
        }
        return "started";
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
