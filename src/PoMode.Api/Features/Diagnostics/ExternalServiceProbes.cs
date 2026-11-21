using System.Diagnostics;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using PoMode.Api.Features.Analysis;
using PoMode.Api.Features.SongIdentity;
using PoMode.Api.Features.SongLoop;
using PoMode.Api.Infrastructure;
using PoMode.Api.Platform;
using PoMode.Shared.Diagnostics;

namespace PoMode.Api.Features.Diagnostics;

/// <summary>
/// The live calls behind the /diag rows for things outside this process: the blob mirror, Key Vault,
/// Application Insights and the free web services the app asks (AcoustID, LRCLIB, Hooktheory, the model
/// hosts). Each answers within <see cref="Budget"/>; a timeout is a finding, never a hang. Rows carry our
/// own fixed sentences only: no host, URL, key or exception text reaches the anonymous payload.
/// </summary>
public sealed class ExternalServiceProbes(
    IHttpClientFactory http,
    IConfiguration configuration,
    SecretSourceInfo secretSource,
    JobBlobStorage blobs,
    AcoustIdClient acoustId,
    HooktheoryClient hooktheory)
{
    public const string HttpClientName = "Diagnostics";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    // Plain reachability: any HTTP answer below 500 means the service is up. None of these needs a key.
    private static readonly Uri AcoustIdProbe = new("https://api.acoustid.org/v2/lookup");
    private static readonly Uri LrclibProbe = new("https://lrclib.net/api/get?artist_name=PoMode&track_name=probe&duration=1");
    private static readonly Uri HuggingFaceProbe = new("https://huggingface.co/api/models?limit=1");
    private static readonly Uri JsDelivrProbe = new("https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/package.json");

    private SecretClient? _vault;

    public async Task<DiagCheckDto[]> ProbeAllAsync(CancellationToken ct) => await Task.WhenAll(
        BlobStorageAsync(ct),
        KeyVaultAsync(ct),
        AppInsightsAsync(ct),
        AcoustIdAsync(ct),
        LrclibAsync(ct),
        HooktheoryAsync(ct),
        ModelHostsAsync(ct));

    private async Task<DiagCheckDto> BlobStorageAsync(CancellationToken ct)
    {
        const string purpose = "Durable copy of every upload and analysis, so a restart or a new server loses nothing.";
        var (status, ms) = await TimedAsync(token => blobs.ProbeAsync(token), BlobMirrorStatus.Unreachable, ct);
        return status switch
        {
            BlobMirrorStatus.Connected => new("blob-storage", "data", "Blob storage", DiagnosticStatus.Healthy, purpose,
                "Answering.", ms, null),
            BlobMirrorStatus.Disabled => new("blob-storage", "data", "Blob storage", DiagnosticStatus.Healthy, purpose,
                "Not used in this environment; files stay on this server's disk.", ms, null),
            _ => new("blob-storage", "data", "Blob storage", DiagnosticStatus.Degraded, purpose,
                "Did not answer; files are kept on this server's disk only.", ms,
                "The blob mirror is unreachable, so a restart could lose recent songs."),
        };
    }

    private async Task<DiagCheckDto> KeyVaultAsync(CancellationToken ct)
    {
        const string purpose = "Holds the app's secrets (service keys, the push key pair).";
        var vaultUri = configuration["KeyVault:VaultUri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            return new("key-vault", "service", "Key Vault", DiagnosticStatus.Healthy, purpose,
                "Not used in this environment; settings come from configuration.", 0, null);
        }
        if (secretSource.FellBack)
        {
            return new("key-vault", "service", "Key Vault", DiagnosticStatus.Degraded, purpose,
                "Settings came from the environment instead.", 0, "The vault could not be read at start-up.");
        }
        _vault ??= new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());
        var (ok, ms) = await TimedAsync(async token =>
        {
            // One page of secret names (never values): proves the vault answers and this app may read it.
            await foreach (var _ in _vault.GetPropertiesOfSecretsAsync(token).AsPages(pageSizeHint: 1))
            {
                return true;
            }
            return true;
        }, false, ct);
        return ok
            ? new("key-vault", "service", "Key Vault", DiagnosticStatus.Healthy, purpose, "Answering.", ms, null)
            : new("key-vault", "service", "Key Vault", DiagnosticStatus.Degraded, purpose,
                "Did not answer just now; the secrets read at start-up are still in use.", ms,
                "The vault did not answer this check.");
    }

    private async Task<DiagCheckDto> AppInsightsAsync(CancellationToken ct)
    {
        const string purpose = "Collects request traces and errors so problems can be found later.";
        var ingestion = IngestionEndpoint(configuration[Telemetry.ConnectionStringKey]);
        if (ingestion is null)
        {
            return new("app-insights", "service", "Application Insights", DiagnosticStatus.Healthy, purpose,
                "Not used in this environment.", 0, null);
        }
        var (code, ms) = await GetStatusAsync(new Uri(ingestion, "v2.1/track"), HttpMethod.Post, ct);
        return Reachable("app-insights", "Application Insights", purpose, code, ms,
            "Telemetry from this server is being lost.");
    }

    private async Task<DiagCheckDto> AcoustIdAsync(CancellationToken ct)
    {
        const string purpose = "Names a song from its sound when its file has no title tags.";
        var (code, ms) = await GetStatusAsync(AcoustIdProbe, HttpMethod.Get, ct);
        var row = Reachable("acoustid", "AcoustID", purpose, code, ms, "Songs are named from their tags only.");
        return row.Status == DiagnosticStatus.Healthy && !acoustId.IsConfigured
            ? row with
            {
                Status = DiagnosticStatus.Degraded,
                Detail = "Reachable, but not set up on this server.",
                Problem = "No AcoustID key is configured; songs are named from their tags only.",
            }
            : row;
    }

    private async Task<DiagCheckDto> LrclibAsync(CancellationToken ct)
    {
        const string purpose = "Published, timed lyrics for a song whose title and artist are known.";
        var (code, ms) = await GetStatusAsync(LrclibProbe, HttpMethod.Get, ct);
        return Reachable("lrclib", "LRCLIB lyrics", purpose, code, ms, "Lyrics are transcribed from the vocals instead.");
    }

    private async Task<DiagCheckDto> HooktheoryAsync(CancellationToken ct)
    {
        const string purpose = "Finds published songs built on the same chord loop.";
        var (code, ms) = await GetStatusAsync(HooktheoryClient.BaseAddress, HttpMethod.Get, ct);
        var row = Reachable("hooktheory", "Hooktheory", purpose, code, ms, "\"Songs on these chords\" is unavailable.");
        return row.Status == DiagnosticStatus.Healthy && !hooktheory.IsConfigured
            ? row with
            {
                Status = DiagnosticStatus.Degraded,
                Detail = "Reachable, but not set up on this server.",
                Problem = "No Hooktheory account is configured; \"Songs on these chords\" is hidden.",
            }
            : row;
    }

    private async Task<DiagCheckDto> ModelHostsAsync(CancellationToken ct)
    {
        const string purpose = "Where the analysis models and browser runtimes are downloaded from (Hugging Face, jsDelivr).";
        var started = Stopwatch.GetTimestamp();
        var answers = await Task.WhenAll(
            GetStatusAsync(HuggingFaceProbe, HttpMethod.Get, ct),
            GetStatusAsync(JsDelivrProbe, HttpMethod.Get, ct));
        var ms = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        var down = answers.Count(answer => answer.Code is not (>= 200 and < 500));
        return down == 0
            ? new("model-hosts", "service", "Model downloads", DiagnosticStatus.Healthy, purpose, "Both hosts answering.", ms, null)
            : new("model-hosts", "service", "Model downloads", DiagnosticStatus.Degraded, purpose,
                $"{down} of {answers.Length} hosts did not answer.", ms,
                "A model that is not downloaded yet cannot be fetched; simpler analysers stand in.");
    }

    private static DiagCheckDto Reachable(string name, string label, string purpose, int? code, double ms, string impact)
        => code switch
        {
            >= 200 and < 500 => new(name, "service", label, DiagnosticStatus.Healthy, purpose, "Answering.", ms, null),
            null => new(name, "service", label, DiagnosticStatus.Degraded, purpose, "Did not answer in time.", ms, impact),
            _ => new(name, "service", label, DiagnosticStatus.Degraded, purpose, "Answered with a server error.", ms, impact),
        };

    /// <summary>The HTTP status code of one request, or null when nothing came back within the budget.</summary>
    private async Task<(int? Code, double Ms)> GetStatusAsync(Uri url, HttpMethod method, CancellationToken ct)
    {
        return await TimedAsync<int?>(async token =>
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json");
            }
            using var response = await http.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            return (int)response.StatusCode;
        }, null, ct);
    }

    private static async Task<(T Value, double Ms)> TimedAsync<T>(Func<CancellationToken, Task<T>> probe, T onFailure, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        T value;
        try
        {
            value = await probe(budget.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unreachable, refused, or too slow: the row says so in our words, the exception stays here.
            value = onFailure;
        }
        return (value, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
    }

    /// <summary>The IngestionEndpoint of an Application Insights connection string, or null.</summary>
    internal static Uri? IngestionEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(pair[1].EndsWith('/') ? pair[1] : pair[1] + "/", UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }
        return new Uri("https://dc.services.visualstudio.com/");
    }
}
