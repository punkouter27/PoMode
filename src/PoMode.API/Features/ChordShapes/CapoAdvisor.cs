using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordShapes;

/// <summary>
/// Which chords a song asks a guitarist for, and the capo that turns most of its playing time into
/// open shapes — the first-position chords a beginner already knows. Weighted by how long each chord
/// sounds, not by how many times it is named, because the chord held for half the song is the one
/// whose shape matters.
/// </summary>
/// <remarks>
/// A capo is only suggested when it moves at least <see cref="MinGain"/> of the song into open shapes;
/// a capo that saves one passing chord is a clamp to fit for nothing. Frets above 7 are not tried: the
/// neck is short enough there that the open shapes stop sounding like themselves. Ukulele shapes are
/// the sounding chords as they are: the advice is a guitarist's.
/// </remarks>
public static class CapoAdvisor
{
    private const int MaxFret = 7;
    private const double MinGain = 0.2;
    private const int MaxChords = 8;

    /// <summary>Open guitar shapes by quality, as the shape root's pitch class.</summary>
    private static readonly Dictionary<string, int[]> OpenShapes = new()
    {
        ["maj"] = [0, 2, 4, 7, 9],       // C D E G A
        ["min"] = [2, 4, 9],             // Dm Em Am
        ["7"] = [0, 2, 4, 7, 9, 11],     // C7 D7 E7 G7 A7 B7
        ["min7"] = [2, 4, 9],            // Dm7 Em7 Am7
        ["maj7"] = [0, 2, 4, 5, 7, 9],   // Cmaj7 Dmaj7 Emaj7 Fmaj7 Gmaj7 Amaj7
    };

    /// <summary>chords-db's suffix and the symbol's for each quality the recognisers write.</summary>
    private static readonly Dictionary<string, (string DbSuffix, string Symbol)> Suffixes = new()
    {
        ["maj"] = ("major", ""),
        ["min"] = ("minor", "m"),
        ["7"] = ("7", "7"),
        ["min7"] = ("m7", "m7"),
        ["maj7"] = ("maj7", "maj7"),
        ["dim"] = ("dim", "dim"),
        ["aug"] = ("aug", "aug"),
    };

    public static ChordShapesDto Advise(IReadOnlyList<ChordSpan> chords)
    {
        var held = chords
            .Where(span => Suffixes.ContainsKey(span.Quality) && RootOf(span.Root) is not null && span.EndSec > span.StartSec)
            .GroupBy(span => (Root: RootOf(span.Root)!.Value, span.Quality))
            .Select(group => (group.Key.Root, group.Key.Quality, group.First().Symbol, Seconds: group.Sum(s => s.EndSec - s.StartSec)))
            .OrderByDescending(chord => chord.Seconds)
            .ToList();
        var total = held.Sum(chord => chord.Seconds);
        if (total <= 0)
        {
            return new ChordShapesDto(0, "", []);
        }

        double OpenShare(int fret) => held
            .Where(chord => IsOpen((chord.Root - fret + 12) % 12, chord.Quality))
            .Sum(chord => chord.Seconds) / total;

        var bare = OpenShare(0);
        var (bestFret, bestShare) = Enumerable.Range(1, MaxFret)
            .Select(fret => (Fret: fret, Share: OpenShare(fret)))
            .OrderByDescending(f => f.Share).ThenBy(f => f.Fret)
            .First();
        var capo = bestShare - bare >= MinGain ? bestFret : 0;

        var shapes = held.Take(MaxChords).Select(chord =>
        {
            var shapeRoot = (chord.Root - capo + 12) % 12;
            var (dbSuffix, symbol) = Suffixes[chord.Quality];
            return new ChordShapeDto(chord.Symbol, ScaleModes.NoteName(shapeRoot) + symbol, chord.Root, shapeRoot,
                dbSuffix, Math.Round(100 * chord.Seconds / total, 1));
        }).ToList();
        return new ChordShapesDto(capo, Advice(capo, bare, bestShare, shapes), shapes);
    }

    private static string Advice(int capo, double bare, double withCapo, IReadOnlyList<ChordShapeDto> shapes)
    {
        if (capo > 0)
        {
            var names = shapes.Select(s => s.ShapeSymbol).Distinct().Take(4).ToList();
            var list = names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
            return $"Capo {capo} and play it as {list} shapes: open chords then cover {Percent(withCapo)} of the song, against {Percent(bare)} without.";
        }
        return bare >= 0.999
            ? "Every chord here is an open shape, so no capo is needed."
            : $"Open shapes cover {Percent(bare)} of the song as it stands, and no capo position does better.";
    }

    private static string Percent(double share)
        => (100 * share).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static bool IsOpen(int shapeRoot, string quality)
        => OpenShapes.TryGetValue(quality, out var roots) && roots.Contains(shapeRoot);

    /// <summary>"C", "F#", "Bb" (or with ♯/♭) as a pitch class; null for "N" and anything else.</summary>
    internal static int? RootOf(string name)
    {
        if (name.Length is 0 or > 2 || "CDEFGAB".IndexOf(char.ToUpperInvariant(name[0])) is var letter && letter < 0)
        {
            return null;
        }
        int[] letters = [0, 2, 4, 5, 7, 9, 11];
        var accidental = name.Length == 1 ? 0 : name[1] switch
        {
            '#' or '♯' => 1,
            'b' or '♭' => -1,
            _ => (int?)null,
        };
        return accidental is null ? null : (letters[letter] + accidental.Value + 12) % 12;
    }
}
