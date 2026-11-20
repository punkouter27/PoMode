using System.Reflection;

namespace PoMode.Api.Platform;

/// <summary>
/// PoMode's platform conventions (log property, cookie stem, liveness, build stamp). This repo owns
/// this file; it follows the Po web standard in AGENTS.md and may change freely.
/// </summary>
/// <remarks>
/// Deliberately dependency-free beyond the shared framework.
/// </remarks>
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

    /// <summary>The build stamp, UTC yyMMdd, from the one <c>BuildVersion</c> assembly metadata key
    /// (Directory.Build.props). The version badge and /api/diag read the same key.</summary>
    public static string Version { get; } =
        typeof(PoPlatform).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value
        ?? "dev";

    /// <summary>The commit this build came from: the SDK stamps the informational version as
    /// "&lt;version&gt;+&lt;sha&gt;". Sent as the X-Commit header so a deploy can prove which build answers.</summary>
    public static string Commit { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
            .Split('+') is [_, var sha, ..] ? sha : "unknown";

    /// <summary>
    /// Maps <c>/health/live</c>: the dependency-free liveness probe (the deploy reads its X-Commit header).
    /// </summary>
    /// <remarks>
    /// Separate from <c>/health</c> on purpose. <c>/health</c> is a DEEP probe whose payload is
    /// app-specific and expensive to poll. This one touches nothing and cannot fail on a dependency
    /// outage, so an uptime monitor can poll it cheaply.
    /// </remarks>
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
