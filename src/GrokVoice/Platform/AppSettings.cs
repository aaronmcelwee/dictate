using Windows.Foundation.Collections;
using Windows.Storage;

namespace GrokVoice.Platform;

/// <summary>Small preferences, stored per user in the package's local settings.</summary>
internal sealed class AppSettings
{
    private readonly IPropertySet _values = ApplicationData.Current.LocalSettings.Values;

    public bool KeepMicWarm
    {
        get => Get(nameof(KeepMicWarm), true);
        set => _values[nameof(KeepMicWarm)] = value;
    }

    /// <summary>Whether Right Alt dictates at the cursor.</summary>
    public bool RightAltEnabled
    {
        get => Get(nameof(RightAltEnabled), true);
        set => _values[nameof(RightAltEnabled)] = value;
    }

    /// <summary>The Grok Bot that Copilot key instructions go to: its id and name, empty until chosen.</summary>
    public string BotId
    {
        get => Get(nameof(BotId), "");
        set => _values[nameof(BotId)] = value;
    }

    public string BotName
    {
        get => Get(nameof(BotName), "");
        set => _values[nameof(BotName)] = value;
    }

    /// <summary>A video clip of a bot's character for the pill, by bot id; empty when none.</summary>
    public string AvatarClip(string botId) => botId.Length == 0 ? "" : Get("AvatarClip_" + botId, "");

    public void SetAvatarClip(string botId, string? path)
    {
        if (botId.Length == 0) return;
        if (string.IsNullOrEmpty(path)) _values.Remove("AvatarClip_" + botId);
        else _values["AvatarClip_" + botId] = path;
    }

    /// <summary>Whether dictations get a clean-up pass from a fast Grok model before they are typed or sent.</summary>
    public bool PolishEnabled
    {
        get => Get(nameof(PolishEnabled), true);
        set => _values[nameof(PolishEnabled)] = value;
    }

    /// <summary>Whether a thin capsule stays at the bottom of the screen while idle.</summary>
    public bool RestingPill
    {
        get => Get(nameof(RestingPill), true);
        set => _values[nameof(RestingPill)] = value;
    }

    /// <summary>Two-letter language code for Grok, or empty to auto-detect.</summary>
    public string Language
    {
        get => Get(nameof(Language), "en");
        set => _values[nameof(Language)] = value;
    }

    private T Get<T>(string key, T fallback) => _values.TryGetValue(key, out var v) && v is T t ? t : fallback;
}
