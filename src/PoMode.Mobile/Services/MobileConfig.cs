namespace PoMode.Mobile.Services;

public sealed class MobileConfig
{
    private const string BaseUrlKey = "pomode_api_base_url";
    private const string ClientIdKey = "pomode_entra_client_id";
    private const string TenantIdKey = "pomode_entra_tenant_id";
    private const string ScopeKey = "pomode_entra_scope";
    private readonly IPreferences _preferences = Preferences.Default;

    /// <summary>The API's Entra app (PoMode:AzureAd:ClientId in the API's appsettings.json); its id token is the bearer.</summary>
    public const string DefaultClientId = "ada13e77-98e6-4fa8-a89d-dd7d0a226021";

    public const string ProductionHost = "app-pomode2.azurewebsites.net";

    /// <summary><c>pomode://auth</c> (sign-in), <c>pomode://song/{jobId}</c> (a finished analysis, from a notification).</summary>
    public const string AppScheme = "pomode";

    public string BaseUrl
    {
        get => _preferences.Get(BaseUrlKey, GetDefaultBaseUrl());
        set => _preferences.Set(BaseUrlKey, NormalizeUrl(value));
    }

    public string EntraClientId
    {
        get => _preferences.Get(ClientIdKey, DefaultClientId);
        set => _preferences.Set(ClientIdKey, string.IsNullOrWhiteSpace(value) ? DefaultClientId : value.Trim());
    }

    public string EntraTenantId
    {
        get => _preferences.Get(TenantIdKey, "common");
        set => _preferences.Set(TenantIdKey, string.IsNullOrWhiteSpace(value) ? "common" : value.Trim());
    }

    public string EntraScope
    {
        get => _preferences.Get(ScopeKey, "openid profile email");
        set => _preferences.Set(ScopeKey, string.IsNullOrWhiteSpace(value) ? "openid profile email" : value.Trim());
    }

    public static string RedirectUri => $"{AppScheme}://auth";

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
