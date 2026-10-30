using System.Globalization;
using AcoustID;
using PoMode.API.Audio;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongIdentity;

/// <summary>
/// Names a recording from its sound: a Chromaprint fingerprint of the first two minutes (AcoustID.NET,
/// the managed port, so no <c>fpcalc</c> binary has to be installed anywhere) looked up at
/// <c>api.acoustid.org/v2/lookup</c>. Off unless <c>PoMode:AcoustId:ClientKey</c> is set (Key Vault).
/// </summary>
/// <remarks>
/// AcoustID's web service is free for non-commercial use only (docs/architecture.md, Song identity).
/// A match counts only from a score of 0.8 and only when the recording carries a title and an artist;
/// a near miss naming the wrong song is worse than the file name.
/// </remarks>
public sealed class AcoustIdClient(IHttpClientFactory http, IConfiguration configuration, ILogger<AcoustIdClient> logger)
{
    public const string HttpClientName = "AcoustID";
    private const string LookupUrl = "https://api.acoustid.org/v2/lookup";

    /// <summary>What <c>fpcalc</c> fingerprints by default, and what the service's own database was built from.</summary>
    private const double FingerprintSeconds = 120;

    internal const double MinScore = 0.8;

    private string? ClientKey => configuration["PoMode:AcoustId:ClientKey"] is { Length: > 0 } value ? value : null;

    public bool IsConfigured => ClientKey is not null;

    public async Task<SongIdentityDto?> LookupAsync(string audioPath, CancellationToken ct)
    {
        if (ClientKey is not { } key || AudioDecoder.TryReadDurationSeconds(audioPath) is not { } duration)
        {
            return null;
        }
        var fingerprint = await Task.Run(() => Fingerprint(audioPath), ct);
        using var form = new FormUrlEncodedContent([
            new("client", key),
            new("duration", Math.Round(duration).ToString(CultureInfo.InvariantCulture)),
            new("fingerprint", fingerprint),
            new("meta", "recordings releasegroups"),
        ]);
        using var response = await http.CreateClient(HttpClientName).PostAsync(LookupUrl, form, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("AcoustID answered {Status}.", (int)response.StatusCode);
            return null;
        }
        return Pick(await response.Content.ReadFromJsonAsync(ExternalJsonContext.Default.AcoustIdLookup, ct));
    }

    private static string Fingerprint(string audioPath)
    {
        var mono = AudioDecoder.ToMono(AudioDecoder.Decode(audioPath, FingerprintSeconds));
        var pcm = new short[mono.Samples.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(mono.Samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
        var context = new ChromaContext();
        context.Start(mono.SampleRate, 1);
        context.Feed(pcm, pcm.Length);
        context.Finish();
        return context.GetFingerprint();
    }

    /// <summary>The best-scoring recording that names both a title and an artist, or null.</summary>
    internal static SongIdentityDto? Pick(LookupResponse? response)
        => (response?.Results ?? [])
            .Where(result => result.Score >= MinScore)
            .OrderByDescending(result => result.Score)
            .SelectMany(result => result.Recordings ?? [])
            .Where(recording => recording.Title is { Length: > 0 } && recording.Artists is { Count: > 0 })
            .Select(recording => new SongIdentityDto(
                recording.Title!,
                string.Join(", ", recording.Artists!.Select(artist => artist.Name)),
                (recording.ReleaseGroups?.FirstOrDefault(group => group.Type == "Album") ?? recording.ReleaseGroups?.FirstOrDefault())?.Title,
                "AcoustID"))
            .FirstOrDefault();

    internal sealed record LookupResponse(string? Status, List<Result>? Results);

    internal sealed record Result(string Id, double Score, List<Recording>? Recordings);

    internal sealed record Recording(string? Title, List<Artist>? Artists, List<ReleaseGroup>? ReleaseGroups);

    internal sealed record Artist(string Name);

    internal sealed record ReleaseGroup(string? Type, string? Title);
}
