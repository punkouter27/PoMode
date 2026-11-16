using PoMode.Shared.Analysis;

namespace PoMode.Api.Features.ChordJams;

/// <summary>
/// A bass line under a written jam, one style per section. Most sections hold the ground: the root
/// on each bar, root and fifth, or (under a busy strum) driving eighths on the root. With the
/// bass slider's chance a section goes further: a walking line that climbs the chord and steps into
/// the next one by a semitone, or a melodic line on syncopated eighths that reaches for the chord's
/// colour notes and jumps the octave, sometimes standing on the third instead of the root.
/// </summary>
/// <remarks>
/// It runs over the finished song and reads only what the song already says (each chord's name for
/// its root and slash bass, its sounding notes for its colour), from a random stream of its own, so a
/// seed's chords, lyrics and everything else are exactly what they were before there was a bass.
/// The notes sit in the bass register, E1 up, timed in eighths from the start of their chord.
/// </remarks>
public static class JamBass
{
    /// <summary>The lowest note a bass line starts a chord on: the open E string of a bass guitar.</summary>
    private const int Floor = 28;

    private enum Style { Root, RootFifth, Pulse, Walking, Melodic }

    private static string Label(Style style) => style switch
    {
        Style.Root => "Root notes",
        Style.RootFifth => "Root and fifth",
        Style.Pulse => "Driving eighths",
        Style.Walking => "Walking",
        _ => "Melodic",
    };

    /// <summary>The jam with a bass line under every chord and its style named on every section.</summary>
    public static ChordJamDto Write(ChordJamDto jam, int seed, double adventure)
    {
        var random = new Random(unchecked((seed * 31) + 0x5BA55));
        var sections = jam.Sections.Select(section =>
        {
            var style = Pick(random, section, adventure);
            var chords = section.Chords;
            var written = chords.Select((chord, i) =>
                chord with { Bass = Line(random, style, chord, chords[(i + 1) % chords.Count]) }).ToList();
            return section with { Chords = written, Bass = Label(style) };
        }).ToList();
        var ending = jam.Ending with { Bass = [new(Register(BassPc(jam.Ending)), 0, Math.Max(1, jam.Ending.Beats * 2))] };
        return jam with { Sections = sections, Ending = ending };
    }

    /// <summary>Adventurous with the slider's chance (never at 0, about a quarter of sections at the
    /// default, nine in ten at the top); otherwise driving eighths under a busy or power-chord section
    /// half the time, else root notes or root and fifth.</summary>
    private static Style Pick(Random random, JamSectionDto section, double adventure)
    {
        if (adventure > 0 && random.NextDouble() < 0.9 * adventure)
        {
            return random.NextDouble() < 0.5 ? Style.Walking : Style.Melodic;
        }
        var busy = section.Sound == "power" || section.Pattern.Count(slot => slot is 'D' or 'U') >= 5;
        if (busy && random.NextDouble() < 0.5)
        {
            return Style.Pulse;
        }
        return random.NextDouble() < 0.5 ? Style.Root : Style.RootFifth;
    }

    private static List<JamBassNoteDto> Line(Random random, Style style, JamChordDto chord, JamChordDto next)
    {
        var eighths = Math.Max(1, chord.Beats * 2);
        var root = Register(BassPc(chord));
        var tones = Tones(chord, root);
        var notes = new List<JamBassNoteDto>();
        void Add(int midi, int at, int length)
        {
            if (at < eighths)
            {
                notes.Add(new(midi, at, Math.Max(1, Math.Min(length, eighths - at))));
            }
        }

        switch (style)
        {
            case Style.Root:
                for (var at = 0; at < eighths; at += 8)
                {
                    Add(root, at, 8);
                }
                break;
            case Style.RootFifth:
                for (var at = 0; at < eighths; at += 4)
                {
                    Add(at % 8 == 0 ? root : root + 7, at, 4);
                }
                break;
            case Style.Pulse:
                for (var at = 0; at < eighths; at++)
                {
                    Add(root, at, 1);
                }
                break;
            case Style.Walking:
                {
                    // Quarter notes up the chord, the last one a semitone off the next chord's bass.
                    var target = Register(BassPc(next));
                    // From below as often as not, but never under the low E string: then from above.
                    var approach = target + (target - 1 >= Floor && random.NextDouble() < 0.6 ? -1 : 1);
                    // An arc rather than a ladder: up the triad, to the colour (or the octave) only on a
                    // long chord, and back down, so the step into the next chord is a step, not a leap.
                    var climb = new[] { root, tones.Third, root + 7, tones.Colour ?? root + 12, root + 7, tones.Third, root + 7 };
                    if (eighths / 2 <= 4)
                    {
                        climb = [root, tones.Third, root + 7];
                    }
                    var beats = eighths / 2;
                    for (var b = 0; b < beats; b++)
                    {
                        Add(b == beats - 1 && beats > 1 ? approach : climb[b % climb.Length], b * 2, 2);
                    }
                    if (eighths % 2 == 1)
                    {
                        Add(root, eighths - 1, 1);
                    }
                    break;
                }
            default:
                {
                    // Syncopated: on 1, the "and" of 2, 3, and the "and" of 4 of each bar, chord tones
                    // and colour, an octave leap now and then; a quarter of the time the third leads.
                    int[] grid = [0, 3, 4, 7];
                    var pool = new List<int> { root, tones.Third, root + 7, root + 12 };
                    if (tones.Colour is { } colour)
                    {
                        pool.Add(colour);
                    }
                    for (var bar = 0; bar < eighths; bar += 8)
                    {
                        for (var g = 0; g < grid.Length; g++)
                        {
                            var at = bar + grid[g];
                            var length = (g + 1 < grid.Length ? grid[g + 1] : 8) - grid[g];
                            var midi = g == 0
                                ? (bar == 0 && random.NextDouble() < 0.25 ? tones.Third : root)
                                : pool[random.Next(pool.Count)];
                            Add(midi, at, length);
                        }
                    }
                    break;
                }
        }
        return notes;
    }

    /// <summary>The chord's third above the bass root (major or minor as the chord sounds it, the fourth
    /// for a sus chord, the fifth for a power chord), and one colour note above the triad if it has one
    /// (a seventh, a ninth, a sixth), in the octave above the root.</summary>
    private static readonly int[] ColourPriority = [10, 11, 2, 9, 6];

    private static (int Third, int? Colour) Tones(JamChordDto chord, int root)
    {
        var rootPc = RootPc(chord.Symbol);
        var intervals = chord.Midi.Select(midi => ((midi % 12) - rootPc + 12) % 12).ToHashSet();
        var third = intervals.Contains(4) ? 4 : intervals.Contains(3) ? 3 : intervals.Contains(5) ? 5 : intervals.Contains(2) ? 2 : 7;
        int? colour = ColourPriority.Where(intervals.Contains).Select(i => (int?)i).FirstOrDefault();
        var shift = (rootPc - (root % 12) + 12) % 12; // the root's offset when a slash bass stands under it
        return (root + shift + third, colour is { } c ? root + shift + c + (c < third ? 12 : 0) : null);
    }

    /// <summary>The bass note a chord stands on: the note after the slash, or else its root.</summary>
    private static int BassPc(JamChordDto chord)
        => chord.Symbol.IndexOf('/') is var slash and >= 0 ? RootPc(chord.Symbol[(slash + 1)..]) : RootPc(chord.Symbol);

    /// <summary>The pitch class of a chord name's letter and accidental (C, F#, Bb, E♭).</summary>
    private static int RootPc(string symbol)
    {
        var pc = "C D EF G A B".IndexOf(char.ToUpperInvariant(symbol[0]), StringComparison.Ordinal);
        if (symbol.Length > 1)
        {
            pc += symbol[1] switch { '#' or '♯' => 1, 'b' or '♭' => -1, _ => 0 };
        }
        return ((pc % 12) + 12) % 12;
    }

    /// <summary>A pitch class in the bass register, E1 to D#2.</summary>
    private static int Register(int pc) => Floor + ((pc - (Floor % 12) + 12) % 12);
}
