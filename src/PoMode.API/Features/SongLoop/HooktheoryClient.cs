using System.Collections.Concurrent;
using System.Net;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongLoop;

/// <summary>
/// Published songs built on the same chords as a song's loop, from Hooktheory's TheoryTab
/// (<c>GET trends/songs</c>). Signs in with the account in <c>PoMode:Hooktheory:Username</c> and
/// <c>:Password</c> (Key Vault); with either missing the feature is off and the page never offers it.
/// </summary>
/// <remarks>
/// Hooktheory's terms forbid copying or redistributing TheoryTab, so nothing is written to the job or
/// the store: an answer is held in memory for an hour, which also keeps the page inside the account's
/// ten requests per ten seconds. Each song keeps its TheoryTab link, and the page links back.
/// The sign-in token lasts about an hour; a 401 signs in again once.
/// </remarks>
public sealed class HooktheoryClient(IHttpClientFactory http, IConfiguration configuration, TimeProvider time, ILogger<HooktheoryClient> logger)
{
    public const string HttpClientName = "Hooktheory";
    public static readonly Uri BaseAddress = new("https://api.hooktheory.com/v1/");

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan TokenFor = TimeSpan.FromMinutes(50);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<ProgressionSongDto> Songs)> _answers = new();
    private (string Value, DateTimeOffset Until)? _token;

    private string? Username => configuration["PoMode:Hooktheory:Username"] is { Length: > 0 } value ? value : null;
    private string? Password => configuration["PoMode:Hooktheory:Password"] is { Length: > 0 } value ? value : null;

    public bool IsConfigured => Username is not null && Password is not null;

    /// <summary>
    /// A loop as Hooktheory's chord path: each chord as a degree of the parent major key (1 is I, 6 is
    /// vi, b7 is the borrowed bVII), repeats collapsed, at most four changes. Null when a chord has no
    /// such name (anything chromatic but bVII, or a quality that is not the degree's own triad), or
    /// when the loop is a single chord, which every song in the catalogue would match.
    /// </summary>
    public static string? ChordPath(IReadOnlyList<string> formulas)
    {
        var path = new List<string>();
        foreach (var formula in formulas)
        {
            if (!SongLoops.TryReadFormula(formula, out var semitones, out var quality))
            {
                return null;
            }
            var triad = quality switch { "min" or "min7" => "min", "dim" => "dim", "aug" => "aug", _ => "maj" };
            var degree = (semitones, triad) switch
            {
                (0, "maj") => "1",
                (2, "min") => "2",
                (4, "min") => "3",
                (5, "maj") => "4",
                (7, "maj") => "5",
                (9, "min") => "6",
                (11, "dim") => "7",
                (10, "maj") => "b7",
                _ => null,
            };
            if (degree is null)
            {
                return null;
            }
            if (path.Count == 0 || path[^1] != degree)
            {
                path.Add(degree);
            }
        }
        if (path.Count > 1 && path[0] == path[^1])
        {
            path.RemoveAt(path.Count - 1);
        }
        return path.Distinct().Count() >= 2 ? string.Join(',', path.Take(4)) : null;
    }

    /// <summary>The first page of songs on <paramref name="chordPath"/>, or null when Hooktheory could
    /// not be asked (not configured, signed out, rate-limited, down).</summary>
    public async Task<IReadOnlyList<ProgressionSongDto>?> SongsAsync(string chordPath, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return null;
        }
        if (_answers.TryGetValue(chordPath, out var held) && time.GetUtcNow() - held.At < CacheFor)
        {
            return held.Songs;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var client = http.CreateClient(HttpClientName);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (await TokenAsync(client, renew: attempt > 0, ct) is not { } token)
                {
                    return null;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, $"trends/songs?cp={Uri.EscapeDataString(chordPath)}");
                request.Headers.Authorization = new("Bearer", token);
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Hooktheory answered {Status} for chord path {Path}.", (int)response.StatusCode, chordPath);
                    return null;
                }
                var songs = (await response.Content.ReadFromJsonAsync(ExternalJsonContext.Default.HooktheorySongs, ct) ?? [])
                    .Where(song => song.Artist is { Length: > 0 } && song.Song is { Length: > 0 }
                        && song.Url?.StartsWith("https://www.hooktheory.com/", StringComparison.Ordinal) == true)
                    .ToList();
                _answers[chordPath] = (time.GetUtcNow(), songs);
                return songs;
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> TokenAsync(HttpClient client, bool renew, CancellationToken ct)
    {
        if (!renew && _token is { } held && held.Until > time.GetUtcNow())
        {
            return held.Value;
        }
        using var response = await client.PostAsJsonAsync("users/auth", new SignIn(Username!, Password!), ExternalJsonContext.Default.HooktheorySignIn, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Hooktheory sign-in answered {Status}.", (int)response.StatusCode);
            _token = null;
            return null;
        }
        var key = (await response.Content.ReadFromJsonAsync(ExternalJsonContext.Default.HooktheorySignedIn, ct))?.Activkey;
        _token = key is { Length: > 0 } ? (key, time.GetUtcNow() + TokenFor) : null;
        return _token?.Value;
    }

    internal sealed record SignIn(string Username, string Password);

    internal sealed record SignedIn(string? Activkey);
}
