using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Lyrics;

/// <summary>
/// A song's published, timed lyrics from LRCLIB (<c>GET lrclib.net/api/get</c>, free and keyless), for
/// a song whose title and artist are known (<see cref="SongIdentity.SongIdentifier"/>). Whisper still
/// reads any song LRCLIB does not have.
/// </summary>
/// <remarks>
/// The song's length goes with the request, and LRCLIB answers only within two seconds of it, so a
/// live take or a radio edit of the same title does not get the album's timings. Only synced lyrics
/// are used: the now-playing chip needs each line's time, and plain text has none.
/// </remarks>
public sealed partial class LrclibClient(IHttpClientFactory http, ILogger<LrclibClient> logger)
{
    public const string HttpClientName = "LRCLIB";
    public static readonly Uri BaseAddress = new("https://lrclib.net/");

    /// <summary>A line is shown no longer than this past its start when nothing follows it soon.</summary>
    private const double MaxLineSeconds = 10;

    /// <summary>The timed lines, an empty list for a track LRCLIB marks instrumental, or null when it
    /// has no synced lyrics for this song at this length.</summary>
    public async Task<IReadOnlyList<LyricLineDto>?> SyncedAsync(SongIdentityDto identity, double durationSec, CancellationToken ct)
    {
        if (identity.Artist is null || durationSec is < 1 or > 3600)
        {
            return null;
        }
        var query = string.Create(CultureInfo.InvariantCulture,
            $"api/get?artist_name={Uri.EscapeDataString(identity.Artist)}&track_name={Uri.EscapeDataString(identity.Title)}&duration={Math.Round(durationSec):0}");
        using var response = await http.CreateClient(HttpClientName).GetAsync(query, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("LRCLIB answered {Status}.", (int)response.StatusCode);
            return null;
        }
        var record = await response.Content.ReadFromJsonAsync(ExternalJsonContext.Default.LrclibRecord, ct);
        return record switch
        {
            { Instrumental: true } => [],
            { SyncedLyrics: { Length: > 0 } lrc } => Parse(lrc, durationSec) is { Count: > 0 } lines ? lines : null,
            _ => null,
        };
    }

    /// <summary>
    /// LRC to timed lines. A line may carry several stamps (a chorus sung twice), metadata tags such as
    /// <c>[ar:…]</c> are skipped, and a stamp with no words is a gap that ends the line before it.
    /// </summary>
    public static List<LyricLineDto> Parse(string lrc, double durationSec)
    {
        var stamped = new List<(double At, string Text)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = Stamp().Matches(raw);
            if (stamps.Count == 0)
            {
                continue;
            }
            var text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match stamp in stamps)
            {
                var at = (int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) * 60)
                    + double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                stamped.Add((at, text));
            }
        }
        stamped.Sort((a, b) => a.At.CompareTo(b.At));

        var lines = new List<LyricLineDto>();
        for (var i = 0; i < stamped.Count; i++)
        {
            var (at, text) = stamped[i];
            if (text.Length == 0 || at >= durationSec)
            {
                continue;
            }
            var next = i + 1 < stamped.Count ? stamped[i + 1].At : durationSec;
            var end = Math.Min(Math.Min(next, at + MaxLineSeconds), durationSec);
            lines.Add(new LyricLineDto(Math.Round(at, 2), Math.Round(end, 2), text));
        }
        return lines;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]")]
    private static partial Regex Stamp();

    internal sealed record Record(bool Instrumental, string? SyncedLyrics);
}
