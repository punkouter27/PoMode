namespace PoMode.Mobile.Services;

public sealed class MobileConfig
{
    private const string BaseUrlKey = "pomode_api_base_url";
    private readonly IPreferences _preferences = Preferences.Default;

    public const string ProductionHost = "app-pomode2.azurewebsites.net";

    /// <summary><c>pomode://song/{jobId}</c> (a finished analysis, from a notification).</summary>
    public const string AppScheme = "pomode";

    public string BaseUrl
    {
        get => _preferences.Get(BaseUrlKey, GetDefaultBaseUrl());
        set => _preferences.Set(BaseUrlKey, NormalizeUrl(value));
    }

    // Debug: scripts/mobile-dev.ps1 runs `adb reverse tcp:5000 tcp:<api port>`; Release talks to production.
    public static string GetDefaultBaseUrl() =>
#if DEBUG
        DeviceInfo.Current.Platform == DevicePlatform.Android ? "http://localhost:5000" : "https://localhost:5001";
#else
        $"https://{ProductionHost}";
#endif

    private static string NormalizeUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? GetDefaultBaseUrl() : trimmed;
    }
}
