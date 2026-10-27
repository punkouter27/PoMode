using System.Text.Json;

namespace PoMode.Client.Services;

/// <summary>One chord-box drawing: per string, low to high, the fret (-1 muted, 0 open) and finger,
/// relative to <see cref="BaseFret"/>, and any barred frets.</summary>
public sealed record ChordFingering(int[] Frets, int[] Fingers, int BaseFret, int[] Barres);

/// <summary>
/// chords-db's fingerings (MIT), served by our own origin from <c>/web-runtime/</c> like the browser
/// model runtime, read once per instrument on first use. chords-db names its keys per instrument
/// ("Csharp" on guitar, "Db" on ukulele), so a chord is looked up by pitch class through the file's own
/// key list rather than by a spelling of ours.
/// </summary>
public sealed class ChordFingerings(HttpClient http)
{
    public const string Guitar = "guitar";
    public const string Ukulele = "ukulele";

    private readonly Dictionary<string, Task<JsonDocument>> _files = [];

    /// <summary>The first (lowest, usually open) position of the chord, or null when chords-db has none.</summary>
    public async Task<ChordFingering?> FindAsync(string instrument, int pitchClass, string suffix)
    {
        if (!_files.TryGetValue(instrument, out var load))
        {
            _files[instrument] = load = LoadAsync(instrument);
        }
        var root = (await load).RootElement;
        var key = root.GetProperty("keys")[pitchClass].GetString()!.Replace("#", "sharp");
        if (!root.GetProperty("chords").TryGetProperty(key, out var chords))
        {
            return null;
        }
        foreach (var chord in chords.EnumerateArray())
        {
            if (chord.GetProperty("suffix").GetString() == suffix
                && chord.GetProperty("positions").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first)
            {
                return new ChordFingering(
                    Ints(first, "frets"), Ints(first, "fingers"),
                    first.TryGetProperty("baseFret", out var baseFret) ? baseFret.GetInt32() : 1,
                    Ints(first, "barres"));
            }
        }
        return null;
    }

    private async Task<JsonDocument> LoadAsync(string instrument)
    {
        await using var stream = await http.GetStreamAsync($"web-runtime/{instrument}.json");
        return await JsonDocument.ParseAsync(stream);
    }

    private static int[] Ints(JsonElement position, string name)
        => position.TryGetProperty(name, out var values) ? [.. values.EnumerateArray().Select(v => v.GetInt32())] : [];
}
