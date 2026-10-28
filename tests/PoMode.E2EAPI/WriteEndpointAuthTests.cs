using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>The write endpoints must refuse unauthenticated callers; reads stay open because the
/// unguessable 128-bit job id is the capability (and browser-native downloads carry no headers).</summary>
public sealed class WriteEndpointAuthTests : IDisposable
{
    private const string SomeJobId = "0123456789abcdef0123456789abcdef";

    // Every route a signed-out caller may reach. There is no FallbackPolicy here, so an endpoint is
    // public unless it says RequireAuthorization; a new public route fails the test below until it
    // is added here on purpose. Static files and the Blazor host are public by design and not listed.
    // Listed as Production maps them: OpenAPI and Scalar exist only in Development. /diag stays public
    // on purpose: presence and health only (no secret values), read by the opt-in Diagnostics page.
    private static readonly string[] AnonymousAllowList =
    [
        "/api/analysis/executors",
        "/api/analysis/interpreters",
        "/api/analysis/{jobId}",
        "/api/analysis/{jobId}/beats",
        "/api/analysis/{jobId}/chords",
        "/api/analysis/{jobId}/chroma",
        "/api/analysis/{jobId}/eta",
        "/api/analysis/{jobId}/interpretation",
        "/api/analysis/{jobId}/lyrics",
        "/api/analysis/{jobId}/midi",
        "/api/analysis/{jobId}/notes",
        "/api/analysis/{jobId}/notes-backing",
        "/api/analysis/{jobId}/notes-chords",
        "/api/analysis/{jobId}/peaks",
        "/api/analysis/{jobId}/preview",
        "/api/analysis/{jobId}/result",
        "/api/analysis/{jobId}/shapes",
        "/api/analysis/{jobId}/stats",
        "/api/analysis/{jobId}/stems",
        "/api/analysis/{jobId}/stems/{name}",
        "/api/analysis/{jobId}/structure",
        "/api/analysis/{jobId}/tempo-map",
        "/api/analysis/{jobId}/visual",
        "/api/analysis/{jobId}/voice",
        "/api/auth/session",
        "/api/chord-jams/random",
        "/api/modal-melodies/describe",
        "/api/modal-melodies/generate",
        "/api/modal-melodies/midi",
        "/api/modal-melodies/progressions",
        "/api/modal-melodies/wav",
        "/auth/guest",
        "/auth/login/microsoft",
        "/auth/logout",
        "/diag/",
        "/health",
        "/health/live",
        "/health/ready",
        "/hubs/analysis",
        "/hubs/analysis/negotiate",
        "/share-target",
        "/web-runtime/{asset}",
        "/{*path:nonfile}",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-auth-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-auth-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:Storage:Mode", "FileSystem")
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    [Fact]
    public async Task Client_result_without_auth_is_401_and_only_the_allow_list_is_anonymous()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/analysis/{SomeJobId}/client-result", new List<NoteEvent>());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // One test rather than two: the E2EAPI suite is at its cap of 25.
        var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                        || e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");
}
