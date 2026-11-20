using System.Reflection;

namespace PoMode.Api.Platform;

/// <summary>The conventions every Po app shares: its name, the session cookie name, the build version
/// and the uniform <c>/health/live</c> probe.</summary>
public static class PoPlatform
{
    /// <summary>This app's name, used as the log property, the cookie stem and the liveness label.</summary>
    public const string AppName = "PoMode";

    /// <summary>Serilog property carrying the app identity, spelled the same in every Po app so a
    /// cross-app query can union them.</summary>
    public const string ApplicationProperty = "Application";

    /// <summary>
    /// Session cookie name. The <c>__Host-</c> prefix is a browser-enforced control: the cookie is
    /// locked to this exact host, must be Secure, and must have Path=/, so a subdomain or a network
    /// attacker cannot overwrite it.
    /// </summary>
    /// <param name="secure">Whether the cookie is issued Secure. Browsers reject the prefix over plain
    /// HTTP, so local http:// runs and the Playwright suites get the unprefixed name.</param>
    public static string SessionCookieName(bool secure) =>
        secure ? $"__Host-{AppName}.Auth" : $"{AppName}.Auth";

    /// <summary>Informational version, from the assembly's build metadata.</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Maps <c>/health/live</c>: dependency-free, the same shape in every Po app, so one dashboard can
    /// poll them all. <c>/health</c> is the deep, app-specific probe.
    /// </summary>
    public static IEndpointRouteBuilder MapPoLiveness(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new
        {
            status = "ok",
            app = AppName,
            version = Version,
            utc = DateTimeOffset.UtcNow,
        }))
        .WithName("PoLiveness")
        .WithTags("Health")
        .AllowAnonymous();

        return endpoints;
    }
}
