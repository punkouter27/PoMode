using PoMode.API.Features.ChordShapes;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

/// <summary>The voicings are written out fret by fret, so a typo would teach the wrong chord, and the
/// jams are random, so a bad combination would only turn up by chance. Each voicing is held to its
/// name (only that chord's notes, all of them but the fifth, the root or slash note lowest, nothing past
/// the 4th fret), and a thousand jams are held to their key.</summary>
public class OpenChordJamsTests
{
    private static readonly Dictionary<string, int[]> Qualities = new()
    {
        [""] = [0, 4, 7], ["m"] = [0, 3, 7], ["6"] = [0, 4, 7, 9], ["7"] = [0, 4, 7, 10], ["maj7"] = [0, 4, 7, 11],
        ["m7"] = [0, 3, 7, 10], ["m9"] = [0, 3, 7, 10, 2], ["add9"] = [0, 4, 7, 2], ["madd9"] = [0, 3, 7, 2],
        ["sus2"] = [0, 2, 7], ["sus4"] = [0, 5, 7], ["7sus4"] = [0, 5, 7, 10], ["maj7#11"] = [0, 4, 7, 11, 6],
        ["m7add11"] = [0, 3, 7, 10, 5], ["maj9"] = [0, 4, 7, 11, 2], ["9"] = [0, 4, 7, 10, 2],
        ["add9add11"] = [0, 4, 7, 2, 5],
    };

    private static int PitchClass(string name)
        => ("C D EF G A B".IndexOf(name[0]) + (name.Length > 1 ? name[1] == '#' ? 1 : -1 : 0) + 12) % 12;

    /// <summary>The root a symbol names, the bass of a slash chord aside: "G/B" is G.</summary>
    private static int RootOf(string symbol)
        => PitchClass(symbol.Length > 1 && symbol[1] is '#' or 'b' ? symbol[..2] : symbol[..1]);

    [Fact]
    public void Every_voicing_sounds_its_name_and_every_jam_stays_in_its_key()
    {
        foreach (var voicing in OpenChordJams.Voicings)
        {
            var (name, bass) = voicing.Symbol.Split('/') is [var n, var b] ? (n, b) : (voicing.Symbol, null);
            var rootLength = name.Length > 1 && name[1] is '#' or 'b' ? 2 : 1;
            var root = PitchClass(name[..rootLength]);
            var expected = Qualities[name[rootLength..]].Select(i => (root + i) % 12).ToHashSet();

            Assert.Equal(root, voicing.Root);
            Assert.True(voicing.PitchClasses.IsSubsetOf(expected), $"{voicing.Symbol} sounds a note outside the chord");
            Assert.True(expected.Except(voicing.PitchClasses).All(pc => pc == (root + 7) % 12), $"{voicing.Symbol} leaves out more than the fifth");
            Assert.Equal(bass is null ? root : PitchClass(bass), voicing.Midi.Min() % 12);
            // A slid shape is the point of going up the neck; it still stays inside one hand's reach.
            Assert.All(voicing.Frets, fret => Assert.InRange(fret, -1, voicing.Slid ? 9 : 4));
        }

        var library = OpenChordJams.Voicings.Select(v => (v.Symbol, string.Join(',', v.Frets))).ToHashSet();
        var keys = new HashSet<string>();
        for (var seed = 0; seed < 1000; seed++)
        {
            var jam = OpenChordJams.Generate(seed);
            keys.Add(jam.Key);
            var (tonic, mode) = jam.Key.Split(' ') is [var t, var m] ? (PitchClass(t), m) : throw new InvalidOperationException(jam.Key);
            var named = mode switch { "major" => ScaleMode.Ionian, "minor" => ScaleMode.Aeolian, _ => Enum.Parse<ScaleMode>(mode) };
            var scale = ScaleModes.Intervals(named).Select(i => (tonic + i) % 12).ToHashSet();
            // The Spanish slide jams are Phrygian over a major home chord: its third is the one borrowed note.
            if (named == ScaleMode.Phrygian)
            {
                scale.Add((tonic + 4) % 12);
            }

            // A modal jam sounds the note that sets its mode apart, somewhere.
            if (named is not (ScaleMode.Ionian or ScaleMode.Aeolian))
            {
                var signature = (tonic + ScaleModes.CharacteristicIntervals(named)[0]) % 12;
                Assert.True(jam.Chords.Any(chord => chord.Midi.Any(midi => midi % 12 == signature)), $"seed {seed}: {jam.Key} never sounds its mode's own note");
            }
            // And home is heard: some chord on the tonic has its third.
            Assert.Contains(jam.Chords, chord => RootOf(chord.Symbol) == tonic && chord.Midi.Any(midi => (midi - tonic + 12) % 12 is 3 or 4));
            Assert.Equal(16, jam.Chords.Sum(chord => chord.Beats));
            Assert.Equal(8, jam.Pattern.Length);
            Assert.All(jam.Pattern, slot => Assert.Contains(slot, "DUB-"));
            Assert.NotEmpty(jam.Feel);
            Assert.All(jam.Chords, chord =>
            {
                Assert.Contains((chord.Symbol, string.Join(',', chord.Frets)), library);
                Assert.True(chord.Midi.All(midi => scale.Contains(midi % 12)), $"seed {seed}: {chord.Symbol} is outside {jam.Key}");
            });
        }
        Assert.Equal(OpenChordJams.Generate(42), OpenChordJams.Generate(42), new JamComparer());
        Assert.True(keys.Count >= 15, $"only {keys.Count} keys came up");
    }

    private sealed class JamComparer : IEqualityComparer<ChordJamDto>
    {
        public bool Equals(ChordJamDto? a, ChordJamDto? b)
            => a!.Name == b!.Name && a.Key == b.Key && a.Pattern == b.Pattern && a.Bpm == b.Bpm
            && a.Chords.Select(c => c.Symbol).SequenceEqual(b.Chords.Select(c => c.Symbol));

        public int GetHashCode(ChordJamDto jam) => jam.Key.GetHashCode();
    }
}
