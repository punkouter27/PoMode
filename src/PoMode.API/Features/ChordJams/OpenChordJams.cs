using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordJams;

/// <summary>
/// The Practice page's open guitar chord jams, made fresh from a seed: each one a short song in a key
/// and mode open chords suit, with a verse, a chorus and a bridge. Each section is a progression shape
/// (I–V–vi–IV, i–VI–III–VII, a Mixolydian I–♭VII–IV; a bridge leaves home and leans back toward it),
/// and for each step an open voicing from <see cref="Voicings"/> — weighted toward the ones with colour
/// (add9, sus, sevenths) and toward the ones that leave strings ringing from the chord before. The
/// fingers stay beginner's fingers; the open strings make the chords sound like more.
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
    /// <summary>Strum patterns from sparse to busy, the strumming slider choosing the set: a down stroke a
    /// beat or two apart, the eighth-note patterns, then constant eighths with few gaps.</summary>
    private static readonly string[][] PatternSets =
    [
        ["D---D---", "B---D---", "B-D-B-D-", "D-D-D-D-"],
        ["D-DU-UDU", "B-DUB-DU", "D-D-DUDU", "B-D-B-DU"],
        ["DUDUDUDU", "D-DUDUDU", "DUDU-UDU", "BUDUBUDU"],
    ];

    /// <summary>The song's order at the middle length, then the tonic chord rung once: verse, chorus,
    /// verse, chorus, bridge, last chorus (see <c>BuildOrder</c> for the others and the pre-chorus).</summary>
    private static readonly string[] Order = ["Verse", "Chorus", "Verse", "Chorus", "Bridge", "Chorus"];

    /// <summary>At full variety: how often a song has a pre-chorus, how often a section runs six or
    /// eight chords instead of four, how often a verse or chorus is voiced afresh when it comes back,
    /// and how often a song's repeat count is drawn rather than twice round. Variety scales them all
    /// linearly from none; the default, a half, is a pre-chorus in about a third of songs.</summary>
    private const double PreChorusChance = 0.7;
    private const double SixChance = 0.3;
    private const double EightChance = 0.3;
    private const double RevoiceChance = 0.7;

    /// <summary>How often a section's chord changes come early or late: a fifth of sections with the
    /// structure slider at plain, three in five at wild.</summary>
    private const double RhythmChance = 0.2;
    private const double RhythmChanceAtFullVariety = 0.6;

    /// <summary>
    /// Each step's length in beats. Mostly every chord gets the same; now and then one or two chords
    /// hand half their length to the chord beside them (6 and 2 beats where both had 4), so a change
    /// comes early or late. The section keeps its length, so it still starts on a bar and the strum
    /// pattern still counts from the top of each.
    /// </summary>
    private static int[] Rhythm(Random random, int steps, int beats, double variety)
    {
        var rhythm = Enumerable.Repeat(beats, steps).ToArray();
        if (steps < 2 || random.NextDouble() >= RhythmChance + ((RhythmChanceAtFullVariety - RhythmChance) * variety))
        {
            return rhythm;
        }
        var shift = Math.Max(1, beats / 2);
        for (var moves = random.NextDouble() < 0.3 ? 2 : 1; moves > 0; moves--)
        {
            var at = random.Next(steps - 1);
            var (from, to) = random.NextDouble() < 0.5 ? (at, at + 1) : (at + 1, at);
            if (rhythm[from] - shift >= 1)
            {
                rhythm[from] -= shift;
                rhythm[to] += shift;
            }
        }
        return rhythm;
    }
    private const double RoundsChance = 1.0;

    /// <summary>What a section is played on: open chords, barre chords up the neck, or power chords.
    /// Each section leaves the open chords with the chance the open-chords slider leaves over, so the
    /// open strings' shimmer has something tighter to stand against; a closed section is power chords
    /// about a third of the time.</summary>
    private enum Sound { Open, Barre, Power }
    private const double PowerChance = 0.3;

    private static Sound[] PickSounds(Random random, int sections, double openChords)
        => [.. Enumerable.Range(0, sections).Select(_ => random.NextDouble() < openChords ? Sound.Open
            : random.NextDouble() < PowerChance ? Sound.Power : Sound.Barre)];

    private static Sound SoundOf(Voicing voicing) => !voicing.Closed ? Sound.Open : voicing.Power ? Sound.Power : Sound.Barre;

    /// <summary>How many times round a four-chord section goes each time it comes up, one setting per
    /// song: twice, or (as often as variety allows) drawn as once, twice or four times.</summary>
    private static int PickRounds(Random random, double variety)
        => random.NextDouble() >= variety * RoundsChance ? 2 : random.NextDouble() switch { < 0.3 => 1, < 0.7 => 2, _ => 4 };

    /// <summary>A longer section, or a pre-chorus (there to build, not to settle), goes round half as often.</summary>
    private static int Repeats(int rounds, string name, int steps) => name == "Pre-chorus" || steps > 4 ? Math.Max(1, rounds / 2) : rounds;

    /// <summary>One open-position chord as fingered, low E string first. A rare one is a shape most
    /// players never meet (a maj9, a 6 with the open B, a slash bass): it comes up now and then, not as
    /// a matter of course, so a jam is mostly chords to learn and occasionally one to discover. A slid one
    /// is an open shape moved up the neck with its open strings left ringing: it belongs to the slide
    /// jams that teach that move and is never picked for a step on its own. A closed one is a movable
    /// shape with no open string (a barre chord or a power chord), only ever picked for a section
    /// played up the neck.</summary>
    public sealed record Voicing(string Symbol, int Root, int[] Frets, int[] Fingers, bool Rare = false, bool Slid = false, bool Closed = false, string? From = null)
    {
        /// <summary>Root and fifth alone: neither major nor minor.</summary>
        public bool Power => Closed && PitchClasses.Count == 2;

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

        // The rare ones: each still open, still within the 4th fret, still only its chord's notes.
        new("Cmaj9", 0, [X, 3, 0, 0, 0, 0], [0, 3, 0, 0, 0, 0], Rare: true),
        new("Cmaj7#11", 0, [X, 3, 2, 0, 0, 2], [0, 3, 2, 0, 0, 1], Rare: true),
        new("C/G", 0, [3, 3, 2, 0, 1, 0], [4, 3, 2, 0, 1, 0], Rare: true),
        new("D6", 2, [X, X, 0, 2, 0, 2], [0, 0, 0, 1, 0, 2], Rare: true),
        new("Emaj7", 4, [0, 2, 1, 1, 0, 0], [0, 3, 1, 2, 0, 0], Rare: true),
        new("E6", 4, [0, 2, 2, 1, 2, 0], [0, 2, 3, 1, 4, 0], Rare: true),
        new("Eadd9", 4, [0, 2, 4, 1, 0, 0], [0, 2, 4, 1, 0, 0], Rare: true),
        new("E/G#", 4, [4, X, 2, 1, 0, 0], [4, 0, 2, 1, 0, 0], Rare: true),
        new("F#m7add11", 6, [2, X, 2, 2, 0, 0], [1, 0, 2, 3, 0, 0], Rare: true),
        new("Aadd9", 9, [X, 0, 2, 4, 2, 0], [0, 0, 1, 4, 2, 0], Rare: true),
        new("Amadd9", 9, [X, 0, 2, 4, 1, 0], [0, 0, 2, 4, 1, 0], Rare: true),
        new("A9", 9, [X, 0, 2, 4, 2, 3], [0, 0, 1, 3, 2, 4], Rare: true),
        new("B7sus4", 11, [X, 2, 2, 2, 0, 0], [0, 1, 2, 3, 0, 0], Rare: true),

        // Slid: the E shape up one fret and up three, the open B and high E kept; the C shape up two,
        // the open G and high E kept. Same fingers as the shape they came from.
        new("Fmaj7#11/E", 5, [0, 3, 3, 2, 0, 0], [0, 2, 3, 1, 0, 0], Slid: true),
        new("G6/E", 7, [0, 5, 5, 4, 0, 0], [0, 2, 3, 1, 0, 0], Slid: true),
        new("Dadd9add11", 2, [X, 5, 4, 0, 3, 0], [0, 3, 2, 0, 1, 0], Slid: true),

        .. Movable(),
        .. SlidOpen(),
    ];

    /// <summary>
    /// Chords found by sliding: each common open shape with its fretted fingers moved up one to five
    /// frets and its open strings left open, the way a player discovers them. What sounds is named from
    /// its notes (root on the bass where that names it, a slash chord otherwise); only the ones that
    /// name as a chord are kept, so each is a new colour for the same hand. They may stand on any step
    /// they fit, like the written-out shapes, and the sentence about a song says where one came from.
    /// </summary>
    private static IEnumerable<Voicing> SlidOpen()
    {
        (string Name, int[] Frets, int[] Fingers)[] shapes =
        [
            ("C", [X, 3, 2, 0, 1, 0], [0, 3, 2, 0, 1, 0]),
            ("A", [X, 0, 2, 2, 2, 0], [0, 0, 1, 2, 3, 0]),
            ("Am", [X, 0, 2, 2, 1, 0], [0, 0, 2, 3, 1, 0]),
            ("D", [X, X, 0, 2, 3, 2], [0, 0, 0, 1, 3, 2]),
            ("Dm", [X, X, 0, 2, 3, 1], [0, 0, 0, 2, 3, 1]),
            ("E", [0, 2, 2, 1, 0, 0], [0, 2, 3, 1, 0, 0]),
            ("Em", [0, 2, 2, 0, 0, 0], [0, 2, 3, 0, 0, 0]),
            ("G", [3, 2, 0, 0, 0, 3], [2, 1, 0, 0, 0, 3]),
            ("Cadd9", [X, 3, 2, 0, 3, 3], [0, 2, 1, 0, 3, 4]),
            ("Asus2", [X, 0, 2, 2, 0, 0], [0, 0, 1, 2, 0, 0]),
            ("Dsus2", [X, X, 0, 2, 3, 0], [0, 0, 0, 1, 3, 0]),
            ("Em7", [0, 2, 2, 0, 3, 0], [0, 1, 2, 0, 3, 0]),
        ];
        // Simplest names first, so a set of notes takes the plainest name it has.
        (string Suffix, int[] Intervals)[] qualities =
        [
            ("", [0, 4, 7]), ("m", [0, 3, 7]), ("sus2", [0, 2, 7]), ("sus4", [0, 5, 7]), ("6", [0, 4, 7, 9]), ("m6", [0, 3, 7, 9]),
            ("7", [0, 4, 7, 10]), ("maj7", [0, 4, 7, 11]), ("m7", [0, 3, 7, 10]), ("add9", [0, 4, 7, 2]), ("madd9", [0, 3, 7, 2]),
            ("add11", [0, 4, 7, 5]), ("madd11", [0, 3, 7, 5]), ("7sus4", [0, 5, 7, 10]), ("6add9", [0, 4, 7, 9, 2]),
            ("maj9", [0, 4, 7, 11, 2]), ("m9", [0, 3, 7, 10, 2]), ("9", [0, 4, 7, 10, 2]), ("maj7#11", [0, 4, 7, 11, 6]),
            ("m7add11", [0, 3, 7, 10, 5]), ("add9add11", [0, 4, 7, 2, 5]),
        ];
        string[] names = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
        // The written-out slid shapes are already in the library under the names their jams teach.
        var seen = new HashSet<string> { "0,3,3,2,0,0", "0,5,5,4,0,0", "-1,5,4,0,3,0" };
        foreach (var (name, frets, fingers) in shapes)
        {
            for (var up = 1; up <= 5; up++)
            {
                var slid = frets.Select(fret => fret > 0 ? fret + up : fret).ToArray();
                if (!seen.Add(string.Join(',', slid)))
                {
                    continue;
                }
                var notes = slid.Select((fret, s) => fret < 0 ? -1 : (Tuning[s] + fret) % 12).Where(pc => pc >= 0).ToHashSet();
                var bass = slid.Select((fret, s) => fret < 0 ? int.MaxValue : Tuning[s] + fret).Min() % 12;
                var named = notes.OrderBy(root => root == bass ? 0 : 1)
                    .SelectMany(root => qualities.Select(quality => (Root: root, quality.Suffix, Set: quality.Intervals.Select(i => (root + i) % 12).ToHashSet())))
                    .FirstOrDefault(c => notes.Count >= 3 && notes.IsSubsetOf(c.Set) && c.Set.Except(notes).All(pc => pc == (c.Root + 7) % 12));
                if (named.Set is null)
                {
                    continue; // a cluster with no name: not a chord to teach
                }
                var symbol = names[named.Root] + named.Suffix + (named.Root == bass ? "" : $"/{names[bass]}");
                yield return new(symbol, named.Root, slid, fingers, Slid: true, From: $"{name} shape up {up} {(up == 1 ? "fret" : "frets")}");
            }
        }
    }

    /// <summary>
    /// The closed shapes, made rather than written out because they are one shape moved: the E-shape
    /// barre with its root on the low E string, the A-shape with its root on the A string, in each
    /// quality the shape holds without an open string, and the two-finger power chord from each. Every
    /// root gets whichever of the two sits between the 1st and 9th fret, so no shape climbs past the
    /// 11th. Roots are spelled as guitarists say them (Bb, not A#), one spelling per chord.
    /// </summary>
    private static IEnumerable<Voicing> Movable()
    {
        string[] roots = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
        // Offsets from the barre fret, low E string first (-1 muted), and the fingers that hold them.
        (string Suffix, int[] Offsets, int[] Fingers)[] eShape =
        [
            ("", [0, 2, 2, 1, 0, 0], [1, 3, 4, 2, 1, 1]),
            ("m", [0, 2, 2, 0, 0, 0], [1, 3, 4, 1, 1, 1]),
            ("7", [0, 2, 0, 1, 0, 0], [1, 3, 1, 2, 1, 1]),
            ("m7", [0, 2, 0, 0, 0, 0], [1, 3, 1, 1, 1, 1]),
            ("5", [0, 2, 2, -1, -1, -1], [1, 3, 4, 0, 0, 0]),
        ];
        (string Suffix, int[] Offsets, int[] Fingers)[] aShape =
        [
            ("", [-1, 0, 2, 2, 2, 0], [0, 1, 2, 3, 4, 1]),
            ("m", [-1, 0, 2, 2, 1, 0], [0, 1, 3, 4, 2, 1]),
            ("7", [-1, 0, 2, 0, 2, 0], [0, 1, 3, 1, 4, 1]),
            ("m7", [-1, 0, 2, 0, 1, 0], [0, 1, 3, 1, 2, 1]),
            ("maj7", [-1, 0, 2, 1, 2, 0], [0, 1, 3, 2, 4, 1]),
            ("sus2", [-1, 0, 2, 2, 0, 0], [0, 1, 3, 4, 1, 1]),
            ("5", [-1, 0, 2, 2, -1, -1], [0, 1, 3, 4, 0, 0]),
        ];
        foreach (var (shapes, open) in new[] { (eShape, Tuning[0]), (aShape, Tuning[1]) })
        {
            for (var root = 0; root < 12; root++)
            {
                var fret = ((root - open) % 12 + 12) % 12;
                if (fret is < 1 or > 9)
                {
                    continue;
                }
                foreach (var (suffix, offsets, fingers) in shapes)
                {
                    yield return new($"{roots[root]}{suffix}", root, [.. offsets.Select(offset => offset < 0 ? X : fret + offset)], fingers, Closed: true);
                }
            }
        }
    }

    /// <summary>
    /// Jams built on one shape slid along the neck while the open strings ring, written out rather than
    /// generated: the E shape walked up a fret and three frets is the Spanish sound (E Phrygian with E
    /// major as home, its raised third the one note the mode borrows, as flamenco does), and the C
    /// shape up two frets is the V of G that keeps G and E sounding. Each is the symbols in order, as
    /// written in <see cref="Voicings"/>, with the move described for the player. The slide is the verse;
    /// the chorus and bridge around it are written out too, in the same key, and <c>Home</c> closes the song.
    /// </summary>
    private static readonly (string Key, string Home, (string Numerals, string[] Chords)[] Sections, string Feel)[] Slides =
    [
        ("E Phrygian", "E",
            [("I – ♭II – ♭III – ♭II", ["E", "Fmaj7#11/E", "G6/E", "Fmaj7#11/E"]),
             ("iv – ♭III – ♭II – I", ["Am", "G6/E", "Fmaj7#11/E", "E"]),
             ("♭vii – iv – ♭II – I", ["Dm", "Am", "Fmaj7", "E7"])],
            "Hold an E chord and slide those three fingers up one fret, then up three, and back: the open B and high E strings never move, and the shape walking over them is the Spanish sound."),
        ("E Phrygian", "E",
            [("iv – ♭III – ♭II – I", ["Am", "G6/E", "Fmaj7#11/E", "E"]),
             ("I – ♭II – ♭III – ♭II", ["E", "Fmaj7#11/E", "G6/E", "Fmaj7#11/E"]),
             ("♭vii – ♭II – ♭III – I", ["Dm", "Fmaj7", "G", "E7"])],
            "The Andalusian cadence, stepping down to home: from Am, make an E shape three frets up, slide it down to one fret up, then down to the open E. The open strings carry the flamenco ring."),
        ("G major", "G",
            [("IV – V – IV – I", ["C", "Dadd9add11", "C", "G"]),
             ("I – IV – vi – V", ["G", "Cadd9", "Em7", "Dsus4"]),
             ("vi – IV – ii – V", ["Em7", "Cadd9", "Am7", "Dadd9add11"])],
            "Play C, then slide every fretted finger up two frets for the V: the open G and high E keep ringing, so it sounds like D with a ninth and an eleventh added for free."),
    ];

    private static readonly string[] SectionNames = ["Verse", "Chorus", "Bridge"];

    /// <summary>How often a jam is a slide jam instead of a generated one.</summary>
    private const double SlideChance = 0.1;

    /// <summary>Keys and modes whose chords open shapes can cover, how often each comes up (major the
    /// most, Lydian the least), progression shapes for each as scale degrees (0 is the tonic) for a
    /// verse or chorus, and bridge shapes: these start away from home and end on the chord that leans
    /// back into it (V, or ♭VII in the modes that have no major V), so the chorus after one lands; a
    /// pre-chorus is drawn from them too.</summary>
    private static readonly (ScaleMode Mode, double Weight, int[] Tonics, int[][] Shapes, int[][] Bridges)[] Keys =
    [
        (ScaleMode.Ionian, 0.35, [7, 0, 2, 9, 4],
            [[0, 4, 5, 3], [0, 3, 5, 4], [5, 3, 0, 4], [0, 5, 3, 4], [0, 3, 0, 4], [0, 2, 3, 4], [3, 0, 4, 5], [1, 4, 0, 0], [0, 4, 3, 3], [0, 3, 1, 4]],
            [[3, 4, 5, 4], [5, 3, 4, 4], [1, 3, 4, 4], [5, 4, 3, 4], [3, 5, 1, 4]]),
        (ScaleMode.Aeolian, 0.25, [4, 9, 2, 11],
            [[0, 5, 2, 6], [0, 6, 5, 6], [0, 3, 6, 2], [0, 5, 6, 0], [5, 6, 0, 0], [0, 2, 6, 3], [0, 4, 5, 3]],
            [[5, 6, 2, 6], [3, 5, 6, 6], [2, 6, 5, 6], [5, 3, 6, 6]]),
        (ScaleMode.Dorian, 0.15, [4, 9, 2],
            [[0, 3, 0, 3], [0, 3, 6, 0], [0, 1, 3, 0], [0, 6, 3, 0]],
            [[2, 3, 6, 6], [6, 2, 3, 3], [1, 2, 3, 3]]),
        (ScaleMode.Mixolydian, 0.15, [7, 2, 9, 4],
            [[0, 6, 3, 0], [0, 4, 6, 3], [0, 6, 0, 3], [3, 6, 0, 0]],
            [[5, 3, 6, 6], [3, 4, 6, 6], [1, 3, 6, 6]]),
        (ScaleMode.Lydian, 0.10, [0, 5, 7],
            [[0, 1, 0, 1], [0, 1, 4, 0], [0, 1, 5, 4]],
            [[5, 4, 1, 1], [2, 5, 1, 1], [4, 5, 1, 1]]),
    ];

    /// <summary>A fresh jam; the same seed and settings always give the same one, so a jam can be found again.</summary>
    public static ChordJamDto Generate(int seed, ChordJamSettings? settings = null)
    {
        var s = settings ?? new ChordJamSettings();
        static double Unit(double value) => Math.Clamp(value, 0, 1);
        var c = new ChordJamSettings(Unit(s.OpenChords), Unit(s.Variety), Unit(s.Mood), Math.Clamp(s.KeyChanges, 0, 3),
            Unit(s.Difficulty), Unit(s.Colour), Math.Clamp(s.ChangeSpeed, 0, 2), Unit(s.Busyness), Unit(s.Tempo),
            Unit(s.Borrowed), Unit(s.Smoothness), Unit(s.Length), Unit(s.Position), Math.Clamp(s.Capo, 0, 7), Unit(s.StrumVariety), Unit(s.SlidChords));
        var random = new Random(seed);
        // The slide jams are written out: all open strings, one key, a mood, a length and a pace of their
        // own, so they give way to any setting that says otherwise.
        var slide = random.NextDouble() < SlideChance * c.OpenChords && c.KeyChanges == 0 && c.Mood is > 0.2 and < 0.8
            && c.Borrowed == 0 && c.ChangeSpeed == 1 && c.Length is >= 0.34 and < 0.67;
        var jam = WithCapo(slide ? Slide(seed, random, c) : Song(seed, random, c), c.Capo);
        var (title, theme, lyrics) = JamLyrics.Write(seed, c.Mood, jam);
        return jam with { Title = title, Theme = theme, Lyrics = lyrics };
    }

    private static ChordJamDto Song(int seed, Random random, ChordJamSettings c)
    {
        var taste = new Taste(c.Difficulty, c.Colour, c.Smoothness, c.Position, c.SlidChords);
        var beats = c.ChangeSpeed switch { 0 => 8, 2 => 2, _ => 4 };
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 500)
            {
                throw new InvalidOperationException("No open-chord key fits any song.");
            }
            var (mode, _, tonics, shapes, bridges) = PickKey(random, c.Mood);
            var tonic = tonics[random.Next(tonics.Length)];
            var preChorus = random.NextDouble() < PreChorusChance * c.Variety;
            var order = BuildOrder(preChorus, c.Length);
            var names = new[] { "Verse", "Pre-chorus", "Chorus", "Bridge" }.Where(order.Contains).ToArray();
            var chorusAt = Array.IndexOf(names, "Chorus");
            // Verse and chorus from the progression shapes, pre-chorus and bridge from the shapes that
            // lean back home; the pre-chorus is always four chords, there only to build. Copied, since
            // borrowing writes into them.
            var parts = names.Select(name => name switch
            {
                "Pre-chorus" => bridges[random.Next(bridges.Length)],
                "Bridge" => Phrase(random, bridges, c.Variety),
                _ => Phrase(random, shapes, c.Variety),
            }).Select(part => part.ToArray()).ToList();
            if (parts[0].SequenceEqual(parts[chorusAt])
                || (preChorus && names.Contains("Bridge") && parts[1].SequenceEqual(parts[Array.IndexOf(names, "Bridge")])))
            {
                continue; // a chorus is not its verse again, nor a bridge its pre-chorus
            }
            var intervals = ScaleModes.Intervals(mode);
            var parallel = Parallel(mode);
            var spelling = Spelling.For(tonic, intervals, parallel);
            Borrow(random, parts, names, intervals, parallel, c.Borrowed);
            var sounds = PickSounds(random, names.Length, c.OpenChords);
            var options = parts.Select((part, p) => part.Select(step => Allowed(StepFits(step, tonic, intervals, parallel, sounds[p]), taste)).ToList()).ToList();
            if (options.Any(part => part.Any(fit => fit.Count == 0)))
            {
                continue; // a step no shape of its sound can play in this key: try another key or shape
            }

            // One chain through the whole song, so each section's first chord rings on from the last one's.
            Voicing? previous = null;
            var chosen = options.Select(part => part.Select(fit => previous = Pick(random, fit, previous, taste)).ToList()).ToList();
            // Home has to be heard, in the verse or chorus: a tonic played only as sus chords never says major or minor.
            var home = new[] { 0, chorusAt }
                .SelectMany(p => Enumerable.Range(0, parts[p].Length).Where(step => parts[p][step] == 0).Select(step => chosen[p][step]))
                .FirstOrDefault(v => v.Third is not null);
            if (home is null)
            {
                continue;
            }
            // A jam named for a mode has to sound like it: somewhere it plays the note that sets the
            // mode apart (Lydian's raised fourth, Dorian's major sixth, Mixolydian's flat seventh), or
            // C Lydian would be C major under another name.
            if (mode is not (ScaleMode.Ionian or ScaleMode.Aeolian)
                && !chosen.SelectMany(part => part).Any(v => v.PitchClasses.Contains((tonic + ScaleModes.CharacteristicIntervals(mode)[0]) % 12)))
            {
                continue;
            }
            var rhythms = parts.Select(part => Rhythm(random, part.Length, beats, c.Variety)).ToList();
            var bars = chosen.Select((part, p) => part.Select((voicing, step) => (Voicing: voicing, Beats: rhythms[p][step])).ToList()).ToList();
            var resolves = sounds[0] == Sound.Open && random.NextDouble() < 0.4 && TrySuspendLastBar(bars[0], options[0][^1]);

            var rounds = PickRounds(random, c.Variety);
            var patterns = PickPatterns(random, names, c.Busyness);
            var numerals = parts.Select(part => string.Join(" – ", part.Select(step => StepNumeral(step, intervals, parallel)))).ToList();
            var written = new Dictionary<string, JamSectionDto>();
            for (var p = 0; p < names.Length; p++)
            {
                written[names[p]] = Section(names[p], numerals[p], patterns[p], Repeats(rounds, names[p], parts[p].Length), bars[p], sounds[p], spelling);
                // Sometimes a verse or chorus comes back on other shapes: the same progression, a new hand.
                if (names[p] is "Verse" or "Chorus" && random.NextDouble() < RevoiceChance * c.Variety && Revoice(random, options[p], chosen[p], taste) is { } again)
                {
                    var againBars = again.Select((voicing, step) => (Voicing: voicing, Beats: rhythms[p][step])).ToList();
                    if (p == 0 && resolves)
                    {
                        TrySuspendLastBar(againBars, options[0][^1]);
                    }
                    written[$"{names[p]} 2"] = Section($"{names[p]} 2", numerals[p], patterns[p], Repeats(rounds, names[p], parts[p].Length), againBars, sounds[p], spelling);
                }
            }

            // Key changes: from each change on, the parts are the same progressions a step or a half
            // step higher ("Chorus in A"), voiced afresh there, on barre chords where open ones run out,
            // and a bar of the new key's V leads into each ("Lift to A").
            var changes = KeyChangePoints(order, c.KeyChanges);
            var shifts = changes.Select(_ => random.NextDouble() < 0.65 ? 2 : 1).ToArray();
            var seen = new HashSet<string>();
            var keys = new List<string>();
            var finalTonic = tonic;
            for (var i = 0; i < order.Length; i++)
            {
                var part = order[i];
                var again = !seen.Add(part);
                var level = changes.Count(change => change <= i);
                if (level == 0)
                {
                    keys.Add(again && written.ContainsKey($"{part} 2") ? $"{part} 2" : part);
                    continue;
                }
                finalTonic = (tonic + shifts.Take(level).Sum()) % 12;
                var keyName = Spelling.For(finalTonic, intervals, null).Of(finalTonic);
                if (changes.Contains(i))
                {
                    var lift = $"Lift to {keyName}";
                    if (!written.ContainsKey(lift))
                    {
                        written[lift] = Lift(random, lift, finalTonic, intervals, taste);
                    }
                    keys.Add(lift);
                }
                var name = $"{part} in {keyName}";
                if (!written.ContainsKey(name))
                {
                    var p = Array.IndexOf(names, part);
                    written[name] = Transpose(random, name, parts[p], numerals[p], patterns[p], Repeats(rounds, part, parts[p].Length), sounds[p], finalTonic, intervals, parallel, rhythms[p], taste);
                }
                keys.Add(name);
            }
            var laid = keys.Distinct().ToList();
            var ending = finalTonic == tonic ? home : Home(finalTonic, intervals);

            var lead = Array.IndexOf(sounds, Sound.Open);
            var sentences = new List<string>();
            if (lead >= 0)
            {
                // The sentence is about the open chords' trick, so it is written from the first open section.
                sentences.Add(Feel(bars[lead].Select(bar => bar.Voicing).ToList(), lead == 0 && resolves));
            }
            sentences.AddRange(Contrast(names, sounds));
            var found = chosen.SelectMany(part => part).Where(v => v.From is not null).DistinctBy(v => v.Symbol).Take(2).ToList();
            if (found.Count > 0)
            {
                sentences.Add(string.Join(" ", found.Select(v => $"{spelling.Respell(v.Symbol)} is the {v.From} with the open strings left ringing: the same fingers, a new chord.")));
            }
            var uneven = Enumerable.Range(0, names.Length).Where(p => rhythms[p].Distinct().Count() > 1).Select(p => names[p].ToLowerInvariant()).ToList();
            if (uneven.Count > 0)
            {
                sentences.Add($"Not every change waits for the bar: in the {List(uneven)} one chord holds longer and the next comes in early, or the other way round, so count the beats under each box.");
            }
            if (parts.Any(part => part.Any(step => step < 0)))
            {
                sentences.Add($"Some chords are borrowed from {(parallel[2] == 3 ? "the minor" : "the major")} key on the same note (the numerals the key would not give, like ♭VI or iv): one note outside the key, and the colour shifts.");
            }
            sentences.AddRange(Modulations(order, changes, shifts, tonic, intervals));
            var key = $"{ScaleModes.NoteName(tonic)} {ModeName(mode)}";
            return new ChordJamDto(
                seed.ToString(System.Globalization.CultureInfo.InvariantCulture), key, string.Join(' ', sentences), PickBpm(random, c.Tempo),
                [.. laid.Select(name => Vary(random, written[name], c.StrumVariety))], [.. keys.Select(k => laid.IndexOf(k))],
                Chord(ending, 4, Spelling.For(finalTonic, intervals, parallel)), 0, key);
        }
    }

    /// <summary>The song's order for the length slider: short (verse, chorus, twice), as written, or long
    /// (a double last chorus); a pre-chorus goes between each verse and the chorus after it.</summary>
    private static string[] BuildOrder(bool preChorus, double length)
    {
        string[] roles = length switch
        {
            < 0.34 => ["Verse", "Chorus", "Verse", "Chorus"],
            < 0.67 => Order,
            _ => [.. Order, "Chorus"],
        };
        var order = new List<string>();
        for (var i = 0; i < roles.Length; i++)
        {
            if (preChorus && roles[i] == "Chorus" && i > 0 && roles[i - 1] == "Verse")
            {
                order.Add("Pre-chorus");
            }
            order.Add(roles[i]);
        }
        return [.. order];
    }

    /// <summary>The key a song borrows from: the major-third modes from the minor on the same tonic, the
    /// minor-third ones from the major.</summary>
    private static IReadOnlyList<int> Parallel(ScaleMode mode)
        => ScaleModes.Intervals(mode is ScaleMode.Ionian or ScaleMode.Lydian or ScaleMode.Mixolydian ? ScaleMode.Aeolian : ScaleMode.Ionian);

    /// <summary>
    /// Swaps some steps for a chord borrowed from the parallel key (♭VI, ♭VII or iv in major; IV or V in
    /// minor), each step with a chance the borrowed slider sets (a third at its top). A borrowed step is
    /// written as <c>-(degree + 1)</c> of the parallel key. Never the first step of a part, a tonic, or the
    /// last step of a pre-chorus or bridge, which has to lean home.
    /// </summary>
    private static void Borrow(Random random, List<int[]> parts, string[] names, IReadOnlyList<int> intervals, IReadOnlyList<int> parallel, double borrowed)
    {
        static int Above(IReadOnlyList<int> scale, int degree, int by) => (scale[(degree + by) % 7] - scale[degree] + 12) % 12;
        var eligible = Enumerable.Range(1, 6)
            .Where(d => Above(parallel, d, 4) == 7 && (parallel[d] != intervals[d] || Above(parallel, d, 2) != Above(intervals, d, 2)))
            .ToArray();
        if (borrowed <= 0 || eligible.Length == 0)
        {
            return;
        }
        for (var p = 0; p < parts.Count; p++)
        {
            for (var step = 1; step < parts[p].Length; step++)
            {
                var leansHome = names[p] is "Pre-chorus" or "Bridge" && step == parts[p].Length - 1;
                if (!leansHome && parts[p][step] != 0 && random.NextDouble() < borrowed / 3)
                {
                    parts[p][step] = -(eligible[random.Next(eligible.Length)] + 1);
                }
            }
        }
    }

    private static List<Voicing> StepFits(int step, int tonic, IReadOnlyList<int> intervals, IReadOnlyList<int> parallel, Sound sound)
    {
        var (degree, scale) = step >= 0 ? (step, intervals) : (-step - 1, parallel);
        return Fits(degree, tonic, scale, scale.Select(i => (tonic + i) % 12).ToHashSet(), sound);
    }

    private static string StepNumeral(int step, IReadOnlyList<int> intervals, IReadOnlyList<int> parallel)
        => step >= 0 ? Numeral(step, intervals) : Numeral(-step - 1, parallel);

    /// <summary>Where a song changes key, as indexes into its order, latest first to go: the last
    /// chorus, then the bridge, then the second verse, so one change is the classic lift into the
    /// final chorus. A short song without a bridge has two places at most.</summary>
    private static int[] KeyChangePoints(string[] order, int count)
    {
        var candidates = new[] { Array.LastIndexOf(order, "Verse"), Array.IndexOf(order, "Bridge"), Array.LastIndexOf(order, "Chorus") }
            .Where(at => at > 0).Distinct().Order().ToArray();
        return [.. candidates[Math.Max(0, candidates.Length - count)..]];
    }

    /// <summary>A part played in another key: the same steps from the new tonic, on its own sound if
    /// every step has a shape there, otherwise on barre chords, which every root has.</summary>
    private static JamSectionDto Transpose(Random random, string name, int[] part, string numerals, string pattern, int repeats, Sound sound,
        int tonic, IReadOnlyList<int> intervals, IReadOnlyList<int> parallel, int[] rhythm, Taste taste)
    {
        var fits = part.Select(step => Allowed(StepFits(step, tonic, intervals, parallel, sound), taste)).ToList();
        if (fits.Any(fit => fit.Count == 0))
        {
            sound = Sound.Barre;
            fits = [.. part.Select(step => StepFits(step, tonic, intervals, parallel, sound))];
        }
        Voicing? previous = null;
        return Section(name, numerals, pattern, repeats, [.. fits.Select((fit, step) => (previous = Pick(random, fit, previous, taste), rhythm[step]))], sound,
            Spelling.For(tonic, intervals, parallel));
    }

    /// <summary>One bar of the new key's fifth chord before a key change, strummed on the beat, so the
    /// ear hears the new home coming before it lands.</summary>
    private static JamSectionDto Lift(Random random, string name, int tonic, IReadOnlyList<int> intervals, Taste taste)
    {
        var scale = intervals.Select(i => (tonic + i) % 12).ToHashSet();
        var open = Allowed(Fits(4, tonic, intervals, scale, Sound.Open), taste);
        var (sound, fits) = open.Count > 0 ? (Sound.Open, open) : (Sound.Barre, Fits(4, tonic, intervals, scale, Sound.Barre));
        var voicing = Pick(random, fits, null, taste);
        return Section(name, Numeral(4, intervals), "D-D-D-D-", 1, [(voicing, 4)], sound, Spelling.For(tonic, intervals, null));
    }

    /// <summary>The tonic chord of a key the song has moved to, with its third: open if there is one.</summary>
    private static Voicing Home(int tonic, IReadOnlyList<int> intervals)
    {
        var scale = intervals.Select(i => (tonic + i) % 12).ToHashSet();
        return Fits(0, tonic, intervals, scale, Sound.Open).Concat(Fits(0, tonic, intervals, scale, Sound.Barre))
            .OrderBy(v => v.From is null ? 0 : 1).First(v => v.Third is not null);
    }

    /// <summary>A capo moves every sound up and no shape: the boxes, names and key stay what the hand
    /// plays, the notes the browser strums go up by the capo's frets, and the song says where it sounds.</summary>
    private static ChordJamDto WithCapo(ChordJamDto jam, int capo)
    {
        if (capo == 0)
        {
            return jam;
        }
        var name = jam.Key.Split(' ', 2);
        var shape = Enumerable.Range(0, 12).First(pc => ScaleModes.NoteName(pc) == name[0]);
        var sounding = $"{ScaleModes.NoteName(shape + capo, shape + capo)} {name[1]}";
        JamChordDto Up(JamChordDto chord) => chord with { Midi = [.. chord.Midi.Select(midi => midi + capo)] };
        return jam with
        {
            Feel = $"Capo on the {Ordinal(capo)} fret: you finger {jam.Key} shapes and it sounds in {sounding}. {jam.Feel}",
            Sections = [.. jam.Sections.Select(section => section with { Chords = [.. section.Chords.Select(Up)] })],
            Ending = Up(jam.Ending),
            Capo = capo,
            SoundingKey = sounding,
        };
    }

    /// <summary>What the sections off the open chords do to the song, one sentence per sound.</summary>
    private static IEnumerable<string> Contrast(string[] names, Sound[] sounds)
    {
        var allClosed = sounds.All(sound => sound != Sound.Open);
        foreach (var sound in new[] { Sound.Barre, Sound.Power })
        {
            var these = Enumerable.Range(0, names.Length).Where(p => sounds[p] == sound).Select(p => names[p].ToLowerInvariant()).ToList();
            if (these.Count == 0)
            {
                continue;
            }
            var every = allClosed && these.Count == names.Length;
            var who = every ? "Every section" : $"The {List(these)}";
            var single = every || these.Count == 1;
            yield return sound == Sound.Power
                ? $"{who} {(single ? "drops" : "drop")} to power chords, root and fifth only: neither major nor minor, a plain hard edge."
                : $"{who} {(single ? "moves" : "move")} up the neck to barre chords: every string fretted, so it sounds tighter and stops dead when you lift the barre.";
        }
    }

    /// <summary>The key changes, as the player meets them.</summary>
    private static IEnumerable<string> Modulations(string[] order, int[] changes, int[] shifts, int tonic, IReadOnlyList<int> intervals)
    {
        var moves = new List<string>();
        var key = tonic;
        for (var c = 0; c < changes.Length; c++)
        {
            key = (key + shifts[c]) % 12;
            var where = changes[c] == Array.LastIndexOf(order, "Chorus") ? "the last chorus"
                : changes[c] == Array.LastIndexOf(order, "Verse") ? "the second verse" : $"the {order[changes[c]].ToLowerInvariant()}";
            moves.Add($"up a {(shifts[c] == 2 ? "whole" : "half")} step into {Spelling.For(key, intervals, null).Of(key)} for {where}");
        }
        if (moves.Count > 0)
        {
            yield return $"It changes key: {string.Join(", then ", moves)}, each led in by a bar of the new key's V, the same progression lifted so it lands with more push.";
        }
    }

    private static JamSectionDto Section(string name, string numerals, string pattern, int repeats, IEnumerable<(Voicing Voicing, int Beats)> bars,
        Sound sound = Sound.Open, Spelling? spelling = null)
        => new(name, numerals, pattern, repeats, [.. bars.Select(bar => Chord(bar.Voicing, bar.Beats, spelling))], sound.ToString().ToLowerInvariant());

    /// <summary>The same steps voiced again for a later time round: every step with another shape that
    /// fits takes one (still weighted by the taste sliders), so the progression is the same and the hand
    /// is not. Null when no step has another shape.</summary>
    private static List<Voicing>? Revoice(Random random, List<List<Voicing>> options, List<Voicing> first, Taste taste)
    {
        if (options.All(fit => fit.Count < 2))
        {
            return null;
        }
        Voicing? previous = null;
        return [.. options.Select((fit, step) => previous = Pick(random,
            fit.Count > 1 ? fit.Where(v => !ReferenceEquals(v, first[step])).ToList() : fit, previous, taste))];
    }

    /// <summary>Lays the song out from its order: each part's section as written, and a verse or chorus
    /// that comes back in its second voicing (<c>Verse 2</c>) when it has one; the sections in order of
    /// first appearance, and the form as indexes into them.</summary>
    private static (List<JamSectionDto> Sections, int[] Form) Assemble(string[] order, Dictionary<string, JamSectionDto> written)
    {
        var seen = new HashSet<string>();
        var keys = order.Select(part => !seen.Add(part) && written.ContainsKey($"{part} 2") ? $"{part} 2" : part).ToList();
        var names = keys.Distinct().ToList();
        return ([.. names.Select(name => written[name])], [.. keys.Select(key => names.IndexOf(key))]);
    }

    /// <summary>A section's progression: mostly one four-chord shape; now and then (as often as variety
    /// allows) six chords (the first two of one shape, then another whole) or eight (two shapes end to
    /// end). The second shape is never the first again, and it ends the section, so a bridge still ends
    /// leaning home.</summary>
    private static int[] Phrase(Random random, int[][] shapes, double variety)
    {
        var first = shapes[random.Next(shapes.Length)];
        var roll = random.NextDouble();
        if (roll >= (SixChance + EightChance) * variety || shapes.Length < 2)
        {
            return first;
        }
        var others = shapes.Where(shape => !ReferenceEquals(shape, first)).ToArray();
        var second = others[random.Next(others.Length)];
        return roll < SixChance * variety ? [.. first[..2], .. second] : [.. first, .. second];
    }

    private static ChordJamDto Slide(int seed, Random random, ChordJamSettings c)
    {
        var (key, home, sections, feel) = Slides[random.Next(Slides.Length)];
        var bpm = PickBpm(random, c.Tempo);
        var rounds = PickRounds(random, c.Variety);
        var patterns = PickPatterns(random, SectionNames, c.Busyness);
        var written = sections.Select((section, p) => Section(SectionNames[p], section.Numerals, patterns[p], Repeats(rounds, SectionNames[p], section.Chords.Length),
            section.Chords.Select<string, (Voicing, int)>(symbol => (Voicings.First(v => v.Symbol == symbol), 4)))).ToDictionary(section => section.Name);
        var (laid, form) = Assemble(Order, written);
        return new ChordJamDto(
            seed.ToString(System.Globalization.CultureInfo.InvariantCulture), key, feel, bpm,
            [.. laid.Select(section => Vary(random, section, c.StrumVariety))], form, Chord(Voicings.First(v => v.Symbol == home), 4), 0, key);
    }

    /// <summary>A strum pattern per section from the busyness slider's set: the chorus a set busier where
    /// there is one and strummed through (no bass picked alone), so it lifts out of the verse; every
    /// other section unlike the verse.</summary>
    private static string[] PickPatterns(Random random, string[] names, double busyness)
    {
        var level = busyness switch { < 0.34 => 0, < 0.67 => 1, _ => 2 };
        var set = PatternSets[level];
        var verse = set[random.Next(set.Length)];
        var others = set.Where(p => p != verse).ToArray();
        var chorus = PatternSets[Math.Min(level + 1, 2)].Where(p => !p.Contains('B')).ToArray();
        return [.. names.Select(name => name switch
        {
            "Verse" => verse,
            "Chorus" => chorus[random.Next(chorus.Length)],
            _ => others[random.Next(others.Length)],
        })];
    }

    /// <summary>
    /// The strum-variety slider at work on one section: with its chance the section's own pattern takes
    /// a twist (one to three strokes changed), and its chords may each take a fill of their own, the
    /// last chord most likely, as a player turns the corner into what comes next. At 0 nothing changes.
    /// </summary>
    private static JamSectionDto Vary(Random random, JamSectionDto section, double variety)
    {
        if (variety <= 0)
        {
            return section;
        }
        var pattern = random.NextDouble() < variety ? Twist(random, section.Pattern, 1 + random.Next(1 + (int)(2 * variety))) : section.Pattern;
        var chords = section.Chords.Select((chord, i) =>
            random.NextDouble() < variety * (i == section.Chords.Count - 1 ? 0.6 : 0.25)
                ? chord with { Pattern = Twist(random, pattern, 1 + random.Next(3)) }
                : chord).ToList();
        return section with { Pattern = pattern, Chords = chords };
    }

    /// <summary>A strum pattern with some strokes changed: a rest becomes a stroke (down on the beat, up
    /// off it), an up stroke becomes a rest, a down stroke an up or a rest. A bass note alone becomes a
    /// down stroke, never the other way, so a pattern strummed through stays strummed through. The bar
    /// still opens on a stroke.</summary>
    private static string Twist(Random random, string pattern, int changes)
    {
        var slots = pattern.ToCharArray();
        for (var n = 0; n < changes; n++)
        {
            var i = random.Next(slots.Length);
            slots[i] = slots[i] switch
            {
                '-' => i % 2 == 0 ? 'D' : 'U',
                'U' => '-',
                'B' => 'D',
                _ => random.NextDouble() < 0.5 ? 'U' : '-',
            };
        }
        if (slots[0] == '-' || slots[0] == 'U')
        {
            slots[0] = 'D';
        }
        var twisted = new string(slots);
        return twisted == pattern ? Twist(random, pattern, 1) : twisted;
    }

    /// <summary>A tempo around where the tempo slider sits (about 60 BPM at the left, 124 at the right),
    /// give or take 16, on an even number.</summary>
    private static double PickBpm(Random random, double tempo)
        => Math.Clamp((2 * (int)Math.Round((60 + (64 * tempo)) / 2)) - 16 + (4 * random.Next(9)), 50, 160);

    /// <summary>A chord as sent: named in the key it is played in when there is one (G#m in E, Abm in
    /// E♭), as the library writes it otherwise (the written-out slide jams, already named for theirs).</summary>
    private static JamChordDto Chord(Voicing voicing, int beats, Spelling? spelling = null)
        => new(spelling?.Respell(voicing.Symbol) ?? voicing.Symbol, voicing.Frets, voicing.Fingers, Barres(voicing), voicing.Midi, beats);

    /// <summary>
    /// Note names for one key: each scale note on its own letter (so E major has G#, never Ab, and E♭ major
    /// has Ab, never G#), the tonic spelled whichever way gives the scale fewer accidentals (Ab major
    /// rather than G# major, which would need an F double sharp; the sharp on a tie). A note outside the
    /// scale is spelled from the parallel scale on the same letter, since that is where a borrowed chord
    /// comes from; anything else falls back to the sharp name.
    /// </summary>
    private sealed class Spelling
    {
        private const string Letters = "CDEFGAB";
        private static readonly int[] Naturals = [0, 2, 4, 5, 7, 9, 11];
        private readonly Dictionary<int, string> _names = [];

        public static Spelling For(int tonic, IReadOnlyList<int> intervals, IReadOnlyList<int>? parallel)
        {
            var letter = Enumerable.Range(0, 7).Where(l => Math.Abs(Offset(l, tonic)) <= 1)
                .OrderBy(l => Cost(l, tonic, intervals)).ThenBy(l => Offset(l, tonic) < 0 ? 1 : 0).First();
            var spelling = new Spelling();
            IReadOnlyList<int>[] scales = parallel is null ? [intervals] : [intervals, parallel];
            foreach (var scale in scales)
            {
                for (var i = 0; i < 7; i++)
                {
                    var pc = (tonic + scale[i]) % 12;
                    var on = (letter + i) % 7;
                    if (Math.Abs(Offset(on, pc)) <= 1)
                    {
                        spelling._names.TryAdd(pc, Name(on, pc));
                    }
                }
            }
            return spelling;
        }

        /// <summary>The symbol with its root, and its bass if it has one, named for this key.</summary>
        public string Respell(string symbol)
        {
            var (chord, bass) = symbol.Split('/') is [var c, var b] ? (c, b) : (symbol, null);
            var root = chord.Length > 1 && chord[1] is '#' or 'b' ? 2 : 1;
            var named = Of(PitchOf(chord[..root])) + chord[root..];
            return bass is null ? named : $"{named}/{Of(PitchOf(bass))}";
        }

        public string Of(int pc) => _names.TryGetValue(pc, out var name) ? name : ScaleModes.NoteName(pc);

        /// <summary>How far a pitch class sits from a letter's natural note, -6 to 5: -1 a flat, 1 a sharp.</summary>
        private static int Offset(int letter, int pc) => ((pc - Naturals[letter] + 18) % 12) - 6;

        private static int Cost(int letter, int tonic, IReadOnlyList<int> intervals)
            => Enumerable.Range(0, 7).Select(i => Math.Abs(Offset((letter + i) % 7, (tonic + intervals[i]) % 12))).Sum(off => off > 1 ? 100 : off);

        private static string Name(int letter, int pc)
        {
            var off = Offset(letter, pc);
            return Letters[letter] + (off > 0 ? new string('#', off) : new string('b', -off));
        }

        private static int PitchOf(string name) => (Naturals[Letters.IndexOf(name[0])] + name.Skip(1).Sum(c => c == '#' ? 1 : -1) + 12) % 12;
    }

    /// <summary>A key and mode, weighted by how often each comes up and leaned by the mood slider: toward
    /// the major-third modes (major, Mixolydian, Lydian) as it rises to happy, toward the minor-third
    /// ones (minor, Dorian) as it falls to sad. At the middle the weights are as written; at either end the
    /// other side's modes are out.</summary>
    private static (ScaleMode Mode, double Weight, int[] Tonics, int[][] Shapes, int[][] Bridges) PickKey(Random random, double mood)
    {
        double Lean(ScaleMode mode) => Math.Max(0, mode switch
        {
            ScaleMode.Ionian or ScaleMode.Lydian or ScaleMode.Mixolydian => 2 * mood,
            _ => 2 * (1 - mood),
        });
        var roll = random.NextDouble() * Keys.Sum(key => key.Weight * Lean(key.Mode));
        foreach (var key in Keys)
        {
            var weight = key.Weight * Lean(key.Mode);
            if (weight > 0 && (roll -= weight) <= 0)
            {
                return key;
            }
        }
        return Keys.Last(key => key.Weight * Lean(key.Mode) > 0); // rounding at the very end of the roll
    }

    /// <summary>The voicings of a section's sound that can stand on a step: rooted on it, every note in
    /// the key, and its third (when it has one) the step's own.</summary>
    private static List<Voicing> Fits(int degree, int tonic, IReadOnlyList<int> intervals, HashSet<int> scale, Sound sound)
    {
        var root = (tonic + intervals[degree]) % 12;
        var third = (intervals[(degree + 2) % 7] - intervals[degree] + 12) % 12;
        var fifth = (intervals[(degree + 4) % 7] - intervals[degree] + 12) % 12;
        return fifth != 7 ? [] : [.. Voicings.Where(v => (!v.Slid || v.From is not null) && SoundOf(v) == sound && v.Root == root && v.PitchClasses.IsSubsetOf(scale) && (v.Third is null || v.Third == third))];
    }

    /// <summary>The sliders that weigh one voicing against another: how hard a shape may be, how much
    /// colour is wanted, how strongly strings should carry over a change, and where on the neck a
    /// closed shape should sit (0 the lowest frets, 1 the 9th), and how often a chord found by sliding
    /// an open shape is played.</summary>
    private sealed record Taste(double Difficulty, double Colour, double Smoothness, double Position, double Slid);

    /// <summary>Weighted: colour counts from a tenth (no colour wanted) to six times (all of it), triple
    /// at the middle; strings left as they were count by a power the smoothness slider sets (squared at
    /// the middle, so a shape that keeps three strings ringing beats one that keeps one by far); the
    /// very shape just played counts for little, so a repeated step changes voicing (G, then G6); a
    /// rare shape counts for a fraction that grows with difficulty, so it turns up in some jams rather
    /// than most; a shape harder than the difficulty allows counts for almost nothing (but is still
    /// there when nothing else fits); and a closed shape counts most near the neck position asked for.</summary>
    private const double RareWeight = 0.12;

    /// <summary>
    /// What a chord found by sliding an open shape counts for against a written one: next to nothing with
    /// the slid-shapes slider off, about a third at its default (now and then), even at the middle, and
    /// twenty times at the top, where a song is made mostly of them wherever one fits.
    /// </summary>
    /// <summary>The fitting shapes the slid-shapes slider allows: all of them, or with it off none found
    /// by sliding, even where that leaves a step with nothing (the song then tries another key or shape).</summary>
    private static List<Voicing> Allowed(List<Voicing> fits, Taste taste) => taste.Slid > 0 ? fits : [.. fits.Where(v => v.From is null)];

    private static double FoundWeight(double slid) => slid <= 0 ? 1e-9 : 0.05 * Math.Pow(400, slid);

    private static Voicing Pick(Random random, List<Voicing> options, Voicing? previous, Taste taste)
    {
        var weights = options.Select(v =>
            (v.HasColour ? 0.1 + (5.8 * taste.Colour) : 1.0)
            * Math.Pow(1 + (previous is null ? 0 : Shared(previous, v)), 4 * taste.Smoothness)
            * (ReferenceEquals(v, previous) ? 0.2 : 1.0)
            * (v.From is not null ? FoundWeight(taste.Slid) : 1.0)
            * (v.Rare ? Math.Max(0.005, RareWeight * 2 * taste.Difficulty) : 1.0)
            * (Hardness(v) > taste.Difficulty + 0.3 ? 0.05 : 1.0)
            * (v.Closed ? Math.Exp(-Math.Abs(v.Frets.Where(f => f > 0).Min() - (1 + (8 * taste.Position))) / 2) : 1.0)).ToArray();
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

    /// <summary>How hard a shape is to hold, 0 to about 1: fingers down, the stretch between the lowest
    /// and highest fretted note, and a barre on top of that. One finger on an open string is 0; a full
    /// E-shape barre is near the top.</summary>
    private static double Hardness(Voicing v)
    {
        var fretted = v.Frets.Where(f => f > 0).ToList();
        var fingers = v.Fingers.Where(f => f > 0).Distinct().Count();
        return (Math.Max(0, fingers - 1) / 3.0 * 0.5)
            + (fretted.Count == 0 ? 0 : (fretted.Max() - fretted.Min()) / 4.0 * 0.3)
            + (v.Closed && !v.Power ? 0.2 : 0);
    }

    /// <summary>Strings sounding the same note in both shapes: what keeps ringing across a change.</summary>
    private static int Shared(Voicing a, Voicing b)
        => Enumerable.Range(0, Tuning.Length).Count(s => a.Frets[s] >= 0 && a.Frets[s] == b.Frets[s]);

    /// <summary>Splits the last bar into a sus and its resolution on the same root (Dsus4 → D), one
    /// finger moving, when the key has both shapes; each takes half the last chord's length.</summary>
    private static bool TrySuspendLastBar(List<(Voicing Voicing, int Beats)> bars, List<Voicing> options)
    {
        var (last, beats) = bars[^1];
        var sus = last.Third is null ? last : options.FirstOrDefault(v => v.Third is null);
        var home = last.Third is not null ? last : options.FirstOrDefault(v => v.Third is not null);
        if (sus is null || home is null)
        {
            return false;
        }
        bars[^1] = (sus, beats / 2);
        bars.Add((home, beats - (beats / 2)));
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
            // A change of chord, not a chord held on into another voicing of itself.
            var best = Enumerable.Range(0, n).Where(i => chords[i].Symbol != chords[(i + 1) % n].Symbol)
                .DefaultIfEmpty(0).MaxBy(i => Shared(chords[i], chords[(i + 1) % n]));
            var next = chords[(best + 1) % n];
            var kept = next.Symbol == chords[best].Symbol ? 0 : Shared(chords[best], next);
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
