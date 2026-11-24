using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Management.Deployment;

namespace GrokVoice.Platform;

/// <summary>
/// Keeps Murmur up to date in place. Every few hours it reads latest.json from the repository's
/// "murmur-latest" release; when a newer build is there it downloads the package and, once the
/// user isn't dictating, installs it over the running app. Windows closes Murmur for the update and
/// starts it again (RegisterApplicationRestart). No PowerShell, no admin: the package is signed with
/// the same certificate as the installed one.
/// </summary>
internal sealed class Updater(DictationHost host)
{
    private static readonly Uri Feed = new("https://github.com/aaronmcelwee/dictate/releases/download/murmur-latest/latest.json");
    private static readonly TimeSpan Every = TimeSpan.FromHours(3);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim _once = new(1, 1);

    /// <summary>What the last check found, for settings ("Up to date", "Installing 0.2.70…").</summary>
    public string Status { get; private set; } = "";
    public event Action? StatusChanged;

    public static Version Current
    {
        get
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(v.Major, v.Minor, v.Build, v.Revision);
        }
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            while (true)
            {
                await CheckAsync(manual: false).ConfigureAwait(false);
                await Task.Delay(Every).ConfigureAwait(false);
            }
        });
    }

    public async Task CheckAsync(bool manual)
    {
        if (!await _once.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            SetStatus("Checking for updates…");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Feed).ConfigureAwait(false));
            var latest = Version.Parse(doc.RootElement.GetProperty("version").GetString()!);
            if (latest <= Current)
            {
                SetStatus($"Up to date ({Current.ToString(3)})");
                return;
            }
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var name = doc.RootElement.GetProperty(arch).GetString()!;
            var package = new Uri(Feed, name);

            SetStatus($"Downloading {latest.ToString(3)}…");
            var folder = Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "update");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder)) TryDelete(old);
            var file = Path.Combine(folder, $"Murmur_{latest}_{arch}.msix");
            await using (var source = await Http.GetStreamAsync(package).ConfigureAwait(false))
            await using (var target = File.Create(file))
                await source.CopyToAsync(target).ConfigureAwait(false);

            // Never cut off a dictation or a reply being read: wait until things are quiet.
            SetStatus($"Installing {latest.ToString(3)} when you're not dictating…");
            while (!host.IdleForUpdate) await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            host.Write($"update {Current} -> {latest}, installing in place");
            RegisterApplicationRestart("--updated", 0);
            var result = await new PackageManager().AddPackageAsync(new Uri(file), null,
                DeploymentOptions.ForceTargetApplicationShutdown);
            // Only reached if Windows didn't need to close Murmur, or the install failed.
            SetStatus(result.IsRegistered ? $"Updated to {latest.ToString(3)}" : $"Update failed: {result.ErrorText}");
        }
        catch (Exception e)
        {
            host.Write($"update check failed: {e.Message}");
            SetStatus(manual ? $"Couldn't check for updates: {e.Message}" : "");
        }
        finally
        {
            _once.Release();
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);
}
