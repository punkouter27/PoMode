using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordShapes;

/// <summary>
/// The Practice page's open guitar chord jams, made fresh from a seed: a key and mode open chords suit,
/// a progression shape (I–V–vi–IV, i–VI–III–VII, a Mixolydian I–♭VII–IV), and for each step an open
/// voicing from <see cref="Voicings"/> — weighted toward the ones with colour (add9, sus, sevenths) and
/// toward the ones that leave strings ringing from the chord before. The fingers stay beginner's
/// fingers; the open strings make the chords sound like more.
/// </summary>
/// <remarks>
/// Every voicing is written out fret by fret, not looked up, because the sound is the voicing: G and
/// Cadd9 only share their top two notes as 320033 and x32033. A voicing may only stand on a step when
/// every note it sounds is in the key and its third (if it has one) is the step's, so a jam never
/// plays a note outside its mode. The notes sent to the browser are derived from the frets in standard
/// tuning, so what plays is exactly what the box shows; a unit test holds each voicing to its name.
/// Patterns are eighth notes over a 4/4 bar: D strums down, U up, B plucks the bass alone, - rests.
/// </remarks>
public static class OpenChordJams
{
    /// <summary>Standard tuning, low E to high E, as MIDI notes.</summary>
    public static readonly int[] Tuning = [40, 45, 50, 55, 59, 64];

    private const int X = -1;
    private static readonly string[] StringNames = ["low E", "A", "D", "G", "B", "high E"];
    private static readonly string[] Patterns = ["D-DU-UDU", "B-DUB-DU", "D-D-DUDU", "B-D-B-DU"];

    /// <summary>One open-position chord as fingered, low E string first.</summary>
    public sealed record Voicing(string Symbol, int Root, int[] Frets, int[] Fingers)
    {
        public int[] Midi { get; } = [.. Frets.Select((fret, s) => fret < 0 ? -1 : Tuning[s] + fret).Where(midi => midi >= 0)];
        public HashSet<int> PitchClasses { get; } = [.. Frets.Select((fret, s) => fret < 0 ? -1 : (Tuning[s] + fret) % 12).Where(pc => pc >= 0)];

        /// <summary>3 minor, 4 major, null for a sus chord with no third.</summary>
        public int? Third => PitchClasses.Contains((Root + 4) % 12) ? 4 : PitchClasses.Contains((Root + 3) % 12) ? 3 : null;

        /// <summary>Anything past the plain triad: the notes that make an easy chord sound like more.</summary>
        public bool HasColour => Third is null || PitchClasses.Any(pc => ((pc - Root + 12) % 12) is not (0 or 3 or 4 or 7));
    }

    public static IReadOnlyList<Voicing> Voicings { get; } =
    [
        new("G", 7, [3, 2, 0, 0, 0, 3], [2, 1, 0, 0, 0, 3]),
        new("G", 7, [3, 2, 0, 0, 3, 3], [2, 1, 0, 0, 3, 4]),
        new("G6", 7, [3, 2, 0, 0, 0, 0], [2, 1, 0, 0, 0, 0]),
        new("Gmaj7", 7, [3, X, 0, 0, 0, 2], [2, 0, 0, 0, 0, 1]),
        new("Gsus2", 7, [3, 0, 0, 0, 3, 3], [2, 0, 0, 0, 3, 4]),
        new("G7", 7, [3, 2, 0, 0, 0, 1], [3, 2, 0, 0, 0, 1]),
        new("G/B", 7, [X, 2, 0, 0, 3, 3], [0, 1, 0, 0, 3, 4]),
        new("C", 0, [X, 3, 2, 0, 1, 0], [0, 3, 2, 0, 1, 0]),
        new("Cadd9", 0, [X, 3, 2, 0, 3, 3], [0, 2, 1, 0, 3, 4]),
        new("Cadd9", 0, [X, 3, 2, 0, 3, 0], [0, 2, 1, 0, 3, 0]),
        new("Cmaj7", 0, [X, 3, 2, 0, 0, 0], [0, 3, 2, 0, 0, 0]),
        new("C6", 0, [X, 3, 2, 2, 1, 0], [0, 4, 2, 3, 1, 0]),
        new("D", 2, [X, X, 0, 2, 3, 2], [0, 0, 0, 1, 3, 2]),
        new("Dsus2", 2, [X, X, 0, 2, 3, 0], [0, 0, 0, 1, 3, 0]),
        new("Dsus4", 2, [X, X, 0, 2, 3, 3], [0, 0, 0, 1, 3, 4]),
        new("Dmaj7", 2, [X, X, 0, 2, 2, 2], [0, 0, 0, 1, 1, 1]),
        new("D7", 2, [X, X, 0, 2, 1, 2], [0, 0, 0, 2, 1, 3]),
        new("D/F#", 2, [2, 0, 0, 2, 3, 2], [1, 0, 0, 2, 4, 3]),
        new("Dm", 2, [X, X, 0, 2, 3, 1], [0, 0, 0, 2, 3, 1]),
        new("Dm7", 2, [X, X, 0, 2, 1, 1], [0, 0, 0, 2, 1, 1]),
        new("E", 4, [0, 2, 2, 1, 0, 0], [0, 2, 3, 1, 0, 0]),
        new("E7", 4, [0, 2, 0, 1, 0, 0], [0, 2, 0, 1, 0, 0]),
        new("Esus4", 4, [0, 2, 2, 2, 0, 0], [0, 2, 3, 4, 0, 0]),
        new("E7sus4", 4, [0, 2, 0, 2, 0, 0], [0, 2, 0, 3, 0, 0]),
        new("Em", 4, [0, 2, 2, 0, 0, 0], [0, 2, 3, 0, 0, 0]),
        new("Em7", 4, [0, 2, 0, 0, 0, 0], [0, 2, 0, 0, 0, 0]),
        new("Em7", 4, [0, 2, 2, 0, 3, 0], [0, 1, 2, 0, 3, 0]),
        new("Em7", 4, [0, 2, 2, 0, 3, 3], [0, 1, 2, 0, 3, 4]),
        new("Em9", 4, [0, 2, 0, 0, 0, 2], [0, 2, 0, 0, 0, 3]),
        new("Emadd9", 4, [0, 2, 2, 0, 0, 2], [0, 2, 3, 0, 0, 1]),
        new("F", 5, [X, X, 3, 2, 1, 1], [0, 0, 3, 2, 1, 1]),
        new("Fmaj7", 5, [X, X, 3, 2, 1, 0], [0, 0, 3, 2, 1, 0]),
        new("Fmaj7#11", 5, [X, X, 3, 2, 0, 0], [0, 0, 3, 2, 0, 0]),
        new("Fadd9", 5, [X, X, 3, 2, 1, 3], [0, 0, 3, 2, 1, 4]),
        new("A", 9, [X, 0, 2, 2, 2, 0], [0, 0, 1, 2, 3, 0]),
        new("Asus2", 9, [X, 0, 2, 2, 0, 0], [0, 0, 1, 2, 0, 0]),
        new("Asus4", 9, [X, 0, 2, 2, 3, 0], [0, 0, 1, 2, 3, 0]),
        new("A7", 9, [X, 0, 2, 0, 2, 0], [0, 0, 2, 0, 3, 0]),
        new("A7sus4", 9, [X, 0, 2, 0, 3, 0], [0, 0, 2, 0, 3, 0]),
        new("Amaj7", 9, [X, 0, 2, 1, 2, 0], [0, 0, 2, 1, 3, 0]),
        new("A6", 9, [X, 0, 2, 2, 2, 2], [0, 0, 1, 1, 1, 1]),
        new("Am", 9, [X, 0, 2, 2, 1, 0], [0, 0, 2, 3, 1, 0]),
        new("Am7", 9, [X, 0, 2, 0, 1, 0], [0, 0, 2, 0, 1, 0]),
        new("B7", 11, [X, 2, 1, 2, 0, 2], [0, 2, 1, 3, 0, 4]),
        new("Bm7", 11, [X, 2, 0, 2, 0, 2], [0, 1, 0, 2, 0, 3]),
        new("Bm7add11", 11, [X, 2, 0, 2, 3, 0], [0, 1, 0, 2, 3, 0]),
        new("F#m7", 6, [2, X, 2, 2, 2, 0], [1, 0, 2, 3, 4, 0]),
        new("C#m7", 1, [X, 4, 2, 1, 0, 0], [0, 4, 2, 1, 0, 0]),
    ];

    /// <summary>Keys and modes whose chords open shapes can cover, how often each comes up (major the
    /// most, Lydian the least), and progression shapes for each as scale degrees (0 is the tonic).</summary>
    private static readonly (ScaleMode Mode, double Weight, int[] Tonics, int[][] Shapes)[] Keys =
    [
        (ScaleMode.Ionian, 0.35, [7, 0, 2, 9, 4],
            [[0, 4, 5, 3], [0, 3, 5, 4], [5, 3, 0, 4], [0, 5, 3, 4], [0, 3, 0, 4], [0, 2, 3, 4], [3, 0, 4, 5], [1, 4, 0, 0], [0, 4, 3, 3], [0, 3, 1, 4]]),
        (ScaleMode.Aeolian, 0.25, [4, 9, 2, 11],
            [[0, 5, 2, 6], [0, 6, 5, 6], [0, 3, 6, 2], [0, 5, 6, 0], [5, 6, 0, 0], [0, 2, 6, 3], [0, 4, 5, 3]]),
        (ScaleMode.Dorian, 0.15, [4, 9, 2],
            [[0, 3, 0, 3], [0, 3, 6, 0], [0, 1, 3, 0], [0, 6, 3, 0]]),
        (ScaleMode.Mixolydian, 0.15, [7, 2, 9, 4],
            [[0, 6, 3, 0], [0, 4, 6, 3], [0, 6, 0, 3], [3, 6, 0, 0]]),
        (ScaleMode.Lydian, 0.10, [0, 5, 7],
            [[0, 1, 0, 1], [0, 1, 4, 0], [0, 1, 5, 4]]),
    ];

    /// <summary>A fresh jam; the same seed always gives the same one, so a jam can be found again.</summary>
    public static ChordJamDto Generate(int seed)
    {
        var random = new Random(seed);
        for (var attempt = 0; ; attempt++)
        {
            var (mode, _, tonics, shapes) = PickKey(random);
            var tonic = tonics[random.Next(tonics.Length)];
            var shape = shapes[random.Next(shapes.Length)];
            var intervals = ScaleModes.Intervals(mode);
            var scale = intervals.Select(i => (tonic + i) % 12).ToHashSet();
            var options = shape.Select(degree => Fits(degree, tonic, intervals, scale)).ToList();
            if (options.Any(fit => fit.Count == 0))
            {
                if (attempt > 500)
                {
                    throw new InvalidOperationException("No open-chord key fits any progression shape.");
                }
                continue; // a step no open shape can play in this key: try another key or shape
            }

            var chosen = new List<Voicing>();
            for (var step = 0; step < shape.Length; step++)
            {
                chosen.Add(Pick(random, options[step], step == 0 ? null : chosen[^1]));
            }
            // Home has to be heard: a tonic played only as sus chords never says major or minor.
            if (!Enumerable.Range(0, shape.Length).Any(step => shape[step] == 0 && chosen[step].Third is not null))
            {
                continue;
            }
            // A jam named for a mode has to sound like it: somewhere it plays the note that sets the
            // mode apart (Lydian's raised fourth, Dorian's major sixth, Mixolydian's flat seventh), or
            // C Lydian would be C major under another name.
            if (mode is not (ScaleMode.Ionian or ScaleMode.Aeolian)
                && !chosen.Any(v => v.PitchClasses.Contains((tonic + ScaleModes.CharacteristicIntervals(mode)[0]) % 12)))
            {
                continue;
            }
            var bars = chosen.Select(voicing => (Voicing: voicing, Beats: 4)).ToList();
            var resolves = random.NextDouble() < 0.4 && TrySuspendLastBar(bars, options[^1]);

            var key = $"{ScaleModes.NoteName(tonic)} {ModeName(mode)}";
            var numerals = string.Join(" – ", shape.Select(degree => Numeral(degree, intervals)));
            return new ChordJamDto(
                seed.ToString(System.Globalization.CultureInfo.InvariantCulture), numerals, key,
                Feel(bars.Select(bar => bar.Voicing).ToList(), resolves),
                72 + (4 * random.Next(11)), Patterns[random.Next(Patterns.Length)],
                [.. bars.Select(bar => new JamChordDto(bar.Voicing.Symbol, bar.Voicing.Frets, bar.Voicing.Fingers,
                    Barres(bar.Voicing), bar.Voicing.Midi, bar.Beats))]);
        }
    }

    private static (ScaleMode Mode, double Weight, int[] Tonics, int[][] Shapes) PickKey(Random random)
    {
        var roll = random.NextDouble() * Keys.Sum(key => key.Weight);
        foreach (var key in Keys)
        {
            if ((roll -= key.Weight) <= 0)
            {
                return key;
            }
        }
        return Keys[^1];
    }

    /// <summary>The voicings that can stand on a step: rooted on it, every note in the key, and its
    /// third (when it has one) the step's own.</summary>
    private static List<Voicing> Fits(int degree, int tonic, IReadOnlyList<int> intervals, HashSet<int> scale)
    {
        var root = (tonic + intervals[degree]) % 12;
        var third = (intervals[(degree + 2) % 7] - intervals[degree] + 12) % 12;
        var fifth = (intervals[(degree + 4) % 7] - intervals[degree] + 12) % 12;
        return fifth != 7 ? [] : [.. Voicings.Where(v => v.Root == root && v.PitchClasses.IsSubsetOf(scale) && (v.Third is null || v.Third == third))];
    }

    /// <summary>Weighted: colour counts triple, strings left as they were count most (squared, so a
    /// shape that keeps three strings ringing beats one that keeps one by far), and the very shape just
    /// played counts for little, so a repeated step changes voicing (G, then G6).</summary>
    private static Voicing Pick(Random random, List<Voicing> options, Voicing? previous)
    {
        var weights = options.Select(v =>
            (v.HasColour ? 3.0 : 1.0)
            * Math.Pow(1 + (previous is null ? 0 : Shared(previous, v)), 2)
            * (ReferenceEquals(v, previous) ? 0.2 : 1.0)).ToArray();
        var roll = random.NextDouble() * weights.Sum();
        for (var i = 0; i < options.Count; i++)
        {
            if ((roll -= weights[i]) <= 0)
            {
                return options[i];
            }
        }
        return options[^1];
    }

    /// <summary>Strings sounding the same note in both shapes: what keeps ringing across a change.</summary>
    private static int Shared(Voicing a, Voicing b)
        => Enumerable.Range(0, Tuning.Length).Count(s => a.Frets[s] >= 0 && a.Frets[s] == b.Frets[s]);

    /// <summary>Splits the last bar into a sus and its resolution on the same root (Dsus4 → D), one
    /// finger moving, when the key has both shapes.</summary>
    private static bool TrySuspendLastBar(List<(Voicing Voicing, int Beats)> bars, List<Voicing> options)
    {
        var last = bars[^1].Voicing;
        var sus = last.Third is null ? last : options.FirstOrDefault(v => v.Third is null);
        var home = last.Third is not null ? last : options.FirstOrDefault(v => v.Third is not null);
        if (sus is null || home is null)
        {
            return false;
        }
        bars[^1] = (sus, 2);
        bars.Add((home, 2));
        return true;
    }

    /// <summary>
    /// Why this jam sounds like more than it is, from its own frets: the open strings and the held
    /// fingers that carry across the changes (all of them, or all but one), and the chords whose
    /// colour notes those strings supply.
    /// </summary>
    private static string Feel(IReadOnlyList<Voicing> chords, bool resolves)
    {
        var sentences = new List<string>();
        var n = chords.Count;
        string Span(int k) => k == n ? (n == 2 ? "both chords" : $"every chord") : $"{Words(k)} of the {Words(n)} chords";

        var held = Enumerable.Range(0, Tuning.Length)
            .Select(s => (String: s, Fret: chords.GroupBy(v => v.Frets[s]).Where(g => g.Key > 0).OrderByDescending(g => g.Count()).FirstOrDefault()))
            .Where(h => h.Fret is not null && h.Fret.Count() >= Math.Max(2, n - 1))
            .ToList();
        if (held.Count > 0)
        {
            var k = held.Min(h => h.Fret!.Count());
            var frets = held.Select(h => h.Fret!.Key).Distinct().ToList();
            var where = frets.Count == 1
                ? $"the {Ordinal(frets[0])} fret of the {List(held.Select(h => StringNames[h.String]))} {(held.Count == 1 ? "string" : "strings")}"
                : List(held.Select(h => $"the {Ordinal(h.Fret!.Key)} fret of the {StringNames[h.String]} string"));
            sentences.Add($"Leave your fingers on {where}: {(held.Count == 1 ? "it stays" : "they stay")} put for {Span(k)}, so {(held.Count == 1 ? "that note rings" : "those notes ring")} on through the changes.");
        }

        var open = Enumerable.Range(0, Tuning.Length)
            .Select(s => (String: s, Count: chords.Count(v => v.Frets[s] == 0)))
            .Where(o => o.Count >= Math.Max(2, n - 1))
            .ToList();
        if (open.Count > 0)
        {
            var k = open.Min(o => o.Count);
            sentences.Add($"The open {List(open.Select(o => StringNames[o.String]))} {(open.Count == 1 ? "string rings" : "strings ring")} in {Span(k)}, a drone that ties the chords together.");
        }

        if (held.Count == 0 && open.Count == 0)
        {
            var best = Enumerable.Range(0, n).MaxBy(i => Shared(chords[i], chords[(i + 1) % n]));
            var next = chords[(best + 1) % n];
            var kept = Shared(chords[best], next);
            sentences.Add(kept >= 2
                ? $"From {chords[best].Symbol} to {next.Symbol}, {Words(kept)} strings do not change at all, so the move is barely a move."
                : "Open strings ring under the fretted notes in every chord, which is what makes simple shapes sound full.");
        }

        var colour = chords.Where(v => v.HasColour && v.Third is not null).Select(v => v.Symbol).Distinct().ToList();
        if (colour.Count > 0)
        {
            sentences.Add($"{List(colour)} {(colour.Count == 1 ? "adds" : "add")} notes past the plain chord, and the open strings supply them for free.");
        }
        if (resolves)
        {
            sentences.Add("The last bar suspends and resolves inside itself: one finger lifts or lands.");
        }
        return string.Join(' ', sentences);
    }

    private static string Words(int n) => n switch { 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>A fret held by one finger across several strings is drawn as a barre.</summary>
    private static int[] Barres(Voicing voicing)
        => [.. Enumerable.Range(0, voicing.Frets.Length)
            .Where(s => voicing.Fingers[s] > 0)
            .GroupBy(s => (voicing.Fingers[s], voicing.Frets[s]))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Item2)];

    /// <summary>The step as a roman numeral, upper case for major, flat or sharp against the major scale.</summary>
    private static string Numeral(int degree, IReadOnlyList<int> intervals)
    {
        string[] romans = ["I", "II", "III", "IV", "V", "VI", "VII"];
        int[] major = [0, 2, 4, 5, 7, 9, 11];
        var accidental = (intervals[degree] - major[degree]) switch { -1 => "♭", 1 => "♯", _ => "" };
        var minor = (intervals[(degree + 2) % 7] - intervals[degree] + 12) % 12 == 3;
        return accidental + (minor ? romans[degree].ToLowerInvariant() : romans[degree]);
    }

    private static string ModeName(ScaleMode mode) => mode switch
    {
        ScaleMode.Ionian => "major",
        ScaleMode.Aeolian => "minor",
        _ => mode.ToString(),
    };

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    private static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        return all.Count switch
        {
            0 => "",
            1 => all[0],
            _ => string.Join(", ", all[..^1]) + " and " + all[^1],
        };
    }
}
