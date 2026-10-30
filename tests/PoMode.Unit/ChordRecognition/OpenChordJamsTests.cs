using PoMode.API.Features.ChordJams;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

/// <summary>The voicings are written out fret by fret, so a typo would teach the wrong chord, and the
/// jams are random, so a bad combination would only turn up by chance. Each voicing is held to its
/// name (only that chord's notes, all of them but the fifth, the root or slash note lowest, nothing past
/// the 4th fret unless it is a slid or closed shape), and a thousand jams are held to their key and to being a song.</summary>
public class OpenChordJamsTests
{
    private static readonly Dictionary<string, int[]> Qualities = new()
    {
        [""] = [0, 4, 7],
        ["m"] = [0, 3, 7],
        ["6"] = [0, 4, 7, 9],
        ["7"] = [0, 4, 7, 10],
        ["maj7"] = [0, 4, 7, 11],
        ["m7"] = [0, 3, 7, 10],
        ["m9"] = [0, 3, 7, 10, 2],
        ["add9"] = [0, 4, 7, 2],
        ["madd9"] = [0, 3, 7, 2],
        ["sus2"] = [0, 2, 7],
        ["sus4"] = [0, 5, 7],
        ["7sus4"] = [0, 5, 7, 10],
        ["maj7#11"] = [0, 4, 7, 11, 6],
        ["m7add11"] = [0, 3, 7, 10, 5],
        ["maj9"] = [0, 4, 7, 11, 2],
        ["9"] = [0, 4, 7, 10, 2],
        ["add9add11"] = [0, 4, 7, 2, 5],
        ["5"] = [0, 7],
        ["m6"] = [0, 3, 7, 9],
        ["add11"] = [0, 4, 7, 5],
        ["madd11"] = [0, 3, 7, 5],
        ["6add9"] = [0, 4, 7, 9, 2],
    };

    private static int PitchClass(string name)
        => ("C D EF G A B".IndexOf(name[0]) + (name.Length > 1 ? name[1] == '#' ? 1 : -1 : 0) + 12) % 12;

    /// <summary>A chord as fingered, whatever its spelling: a song names a chord for its key (G#m in E,
    /// Abm in E♭), the library one way, so they are matched by root and bass as pitches, quality and frets.</summary>
    private static string Shape(string symbol, int[] frets)
    {
        var (chord, bass) = symbol.Split('/') is [var c, var b] ? (c, b) : (symbol, null);
        var root = chord.Length > 1 && chord[1] is '#' or 'b' ? 2 : 1;
        return $"{PitchClass(chord[..root])}{chord[root..]}/{(bass is null ? "" : PitchClass(bass))}:{string.Join(',', frets)}";
    }

    private static string Shape(OpenChordJams.Voicing voicing) => Shape(voicing.Symbol, voicing.Frets);

    /// <summary>The root a symbol names, the bass of a slash chord aside: "G/B" is G.</summary>
    private static int RootOf(string symbol)
        => PitchClass(symbol.Length > 1 && symbol[1] is '#' or 'b' ? symbol[..2] : symbol[..1]);

    /// <summary>Settings with something to count: some barre and power sections, some surprises.</summary>
    private static readonly ChordJamSettings Lively = new(OpenChords: 0.7, Variety: 0.5);

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
            // A slid shape is the point of going up the neck; it still stays inside one hand's reach. A
            // closed one is a barre or power chord: no open string, nothing past the 11th fret.
            Assert.All(voicing.Frets, fret => Assert.InRange(fret, voicing.Closed && fret >= 0 ? 1 : -1, voicing.Closed ? 11 : voicing.Slid ? 9 : 4));
        }

        var library = OpenChordJams.Voicings.DistinctBy(Shape).ToDictionary(Shape);
        var allOpen = 0;
        // Sliding the open shapes finds chords worth teaching, each with the same fingers as its source.
        Assert.True(OpenChordJams.Voicings.Count(v => v.From is not null) >= 15, "too few chords found by sliding open shapes");
        var keys = new HashSet<string>();
        var lengths = new HashSet<int>();
        var themes = new HashSet<string>();
        var preChoruses = 0;
        var revoiced = 0;
        var rounds = new HashSet<int>();
        for (var seed = 0; seed < 1000; seed++)
        {
            var jam = OpenChordJams.Generate(seed, Lively);
            keys.Add(jam.Key);
            var (tonic, mode) = jam.Key.Split(' ') is [var t, var m] ? (PitchClass(t), m) : throw new InvalidOperationException(jam.Key);
            var named = mode switch { "major" => ScaleMode.Ionian, "minor" => ScaleMode.Aeolian, _ => Enum.Parse<ScaleMode>(mode) };
            var scale = ScaleModes.Intervals(named).Select(i => (tonic + i) % 12).ToHashSet();
            // The Spanish slide jams are Phrygian over a major home chord: its third is the one borrowed note.
            if (named == ScaleMode.Phrygian)
            {
                scale.Add((tonic + 4) % 12);
            }

            var chords = jam.Sections.SelectMany(section => section.Chords).Append(jam.Ending).ToList();

            // A modal jam sounds the note that sets its mode apart, somewhere.
            if (named is not (ScaleMode.Ionian or ScaleMode.Aeolian))
            {
                var signature = (tonic + ScaleModes.CharacteristicIntervals(named)[0]) % 12;
                Assert.True(chords.Any(chord => chord.Midi.Any(midi => midi % 12 == signature)), $"seed {seed}: {jam.Key} never sounds its mode's own note");
            }
            // A song: verse, maybe a pre-chorus, chorus and bridge, each four, six or eight bars of a
            // bar-long pattern, the chorus not the verse again, played in an order that uses them all and
            // ends on a chorus, and then on home with its third. A verse or chorus that comes back on
            // other shapes ("Verse 2") is the same progression with some chord changed.
            var names = jam.Sections.Select(section => section.Name).ToList();
            var parts = names.Where(name => !name.EndsWith(" 2", StringComparison.Ordinal)).ToList();
            Assert.True(parts.SequenceEqual(["Verse", "Chorus", "Bridge"]) || parts.SequenceEqual(["Verse", "Pre-chorus", "Chorus", "Bridge"]), string.Join(", ", names));
            preChoruses += parts.Count - 3;
            foreach (var again in jam.Sections.Where(section => section.Name.EndsWith(" 2", StringComparison.Ordinal)))
            {
                revoiced++;
                var first = jam.Sections[names.IndexOf(again.Name[..^2])];
                Assert.Equal(first.Numerals, again.Numerals);
                Assert.False(first.Chords.Select(c => string.Join(',', c.Frets)).SequenceEqual(again.Chords.Select(c => string.Join(',', c.Frets))), $"seed {seed}: {again.Name} is {first.Name} again");
            }
            Assert.NotEqual(jam.Sections[0].Numerals, jam.Sections[names.IndexOf("Chorus")].Numerals);
            Assert.Equal(Enumerable.Range(0, names.Count), jam.Form.Distinct().Order());
            Assert.StartsWith("Chorus", names[jam.Form[^1]], StringComparison.Ordinal);
            rounds.Add(jam.Sections[0].Repeats);
            // At the default slider some sections leave the open chords, and a section's chords are all of
            // the sound it says: open, barre or power.
            allOpen += jam.Sections.All(section => section.Sound == "open") ? 1 : 0;
            Assert.All(jam.Sections, section => Assert.All(section.Chords, chord =>
            {
                var voicing = library[Shape(chord.Symbol, chord.Frets)];
                Assert.Equal(section.Sound, !voicing.Closed ? "open" : voicing.Power ? "power" : "barre");
            }));
            Assert.True(RootOf(jam.Ending.Symbol) == tonic && jam.Ending.Midi.Any(midi => (midi - tonic + 12) % 12 is 3 or 4), $"seed {seed}: ends on {jam.Ending.Symbol}");
            Assert.All(jam.Sections, section =>
            {
                Assert.Contains(section.Chords.Sum(chord => chord.Beats), new[] { 16, 24, 32 });
                lengths.Add(section.Chords.Sum(chord => chord.Beats) / 4);
                Assert.Equal(8, section.Pattern.Length);
                Assert.All(section.Pattern, slot => Assert.Contains(slot, "DUB-"));
            });
            Assert.All(jam.Sections, section =>
            {
                var accidentals = section.Chords.SelectMany(chord => chord.Symbol.Split('/').Select(name => name.Length > 1 ? name[1] : ' '))
                    .Where(c => c is '#' or 'b').Distinct().Count();
                Assert.True(accidentals < 2, $"seed {seed}: {section.Name} in {jam.Key} mixes sharps and flats: {string.Join(' ', section.Chords.Select(chord => chord.Symbol))}");
            });
            Assert.NotEmpty(jam.Feel);

            // Words for every chord played: a line per chord per time round, the chorus sung the same each
            // time, a verse saying something new when it comes back, a lift chord silent.
            Assert.NotNull(jam.Lyrics);
            themes.Add(jam.Theme!);
            Assert.False(string.IsNullOrWhiteSpace(jam.Title));
            Assert.Equal(jam.Form.Count, jam.Lyrics!.Count);
            for (var f = 0; f < jam.Form.Count; f++)
            {
                var section = jam.Sections[jam.Form[f]];
                Assert.Equal(section.Repeats * section.Chords.Count, jam.Lyrics[f].Count);
                Assert.All(jam.Lyrics[f].Where((_, line) => section.Chords[line % section.Chords.Count].Beats >= 4), line => Assert.False(string.IsNullOrWhiteSpace(line)));
            }
            var choruses = Enumerable.Range(0, jam.Form.Count).Where(f => jam.Sections[jam.Form[f]].Name.StartsWith("Chorus", StringComparison.Ordinal)).ToList();
            Assert.All(choruses, f => Assert.Equal(jam.Lyrics[choruses[0]], jam.Lyrics[f]));
            var verses = Enumerable.Range(0, jam.Form.Count).Where(f => jam.Sections[jam.Form[f]].Name.StartsWith("Verse", StringComparison.Ordinal)).ToList();
            Assert.NotEqual(jam.Lyrics[verses[0]], jam.Lyrics[verses[^1]]);
            Assert.All(chords, chord =>
            {
                Assert.Contains(Shape(chord.Symbol, chord.Frets), library.Keys);
                Assert.True(chord.Midi.All(midi => scale.Contains(midi % 12)), $"seed {seed}: {chord.Symbol} is outside {jam.Key}");
            });
        }
        Assert.Equal(OpenChordJams.Generate(42, Lively), OpenChordJams.Generate(42, Lively), new JamComparer());

        // A take sung over a song is analysed against the song's chords laid end to end from bar one at
        // the tempo it was played: every chord every time round, then the last, with no gap or overlap,
        // each folded to a quality the mode engine voices.
        var sung = OpenChordJams.Generate(7, Lively);
        var spans = JamTakeSeeder.Lay(sung, 90);
        Assert.Equal(sung.Form.Sum(f => sung.Sections[f].Repeats * sung.Sections[f].Chords.Count) + 1, spans.Count);
        var totalBeats = sung.Form.Sum(f => sung.Sections[f].Repeats * sung.Sections[f].Chords.Sum(chord => chord.Beats)) + sung.Ending.Beats;
        Assert.Equal((0.0, totalBeats * 60.0 / 90), (spans[0].StartSec, Math.Round(spans[^1].EndSec, 6)));
        Assert.All(spans.Zip(spans.Skip(1)), pair => Assert.Equal(Math.Round(pair.First.EndSec, 9), Math.Round(pair.Second.StartSec, 9)));
        Assert.All(spans, span => Assert.Contains(span.Quality, ChordQualities.All));
        Assert.Null(JamTakeSeeder.Problem(sung, 90));
        Assert.NotNull(JamTakeSeeder.Problem(sung, 10));
        Assert.Equal(("F#", ChordQualities.Minor7), JamTakeSeeder.Parse("F#m7add11"));
        Assert.Equal(("Bb", ChordQualities.Major7), JamTakeSeeder.Parse("Bbmaj7#11/A"));
        Assert.Equal(("A", ChordQualities.Dominant7), JamTakeSeeder.Parse("A7sus4"));
        Assert.Equal(("C", ChordQualities.Major), JamTakeSeeder.Parse("Cadd9"));
        Assert.Equal(("D", ChordQualities.Minor), JamTakeSeeder.Parse("Dmadd9"));
        // Takes over one song are found again by its fingerprint: the same song made again, or played at
        // another tempo, keeps it; another song does not share it.
        Assert.Matches("^[0-9a-f]{16}$", JamSongs.Fingerprint(sung));
        Assert.Equal(JamSongs.Fingerprint(sung), JamSongs.Fingerprint(OpenChordJams.Generate(7, Lively)));
        Assert.Equal(JamSongs.Fingerprint(sung), JamSongs.Fingerprint(sung with { Bpm = sung.Bpm + 20 }));
        Assert.NotEqual(JamSongs.Fingerprint(sung), JamSongs.Fingerprint(OpenChordJams.Generate(8, Lively)));
        // The defaults are a simple song: open chords, verse–chorus–bridge in four-chord sections, no capo.
        for (var seed = 0; seed < 50; seed++)
        {
            var simple = OpenChordJams.Generate(seed);
            Assert.Equal(new[] { "Verse", "Chorus", "Bridge" }, simple.Sections.Select(section => section.Name));
            Assert.All(simple.Sections, section => Assert.Equal(("open", 16), (section.Sound, section.Chords.Sum(chord => chord.Beats))));
            Assert.Equal((0, simple.Key), (simple.Capo, simple.SoundingKey));
        }
        Assert.True(keys.Count >= 15, $"only {keys.Count} keys came up");
        Assert.True(themes.Count >= 5, $"only {themes.Count} lyric themes came up");
        // Mostly four chords a section, sometimes six or eight; a pre-chorus in some songs, not most.
        Assert.Equal(new[] { 4, 6, 8 }, lengths.Order());
        Assert.InRange(preChoruses, 150, 500);
        // Some sections come back revoiced; a song's parts go round once, twice or four times.
        Assert.InRange(revoiced, 200, 900);
        Assert.Equal(new[] { 1, 2, 4 }, rounds.Order());
        Assert.InRange(allOpen, 200, 450);
    }

    /// <summary>Each slider does what it says at its ends: no open chords at 0 and only open ones at 1,
    /// the plain form at no variety, no major-third key when sad and no minor-third one when happy, and
    /// as many key changes as asked, each section's chords in its own key and the song ending home in
    /// the last one.</summary>
    [Fact]
    public void The_sliders_shape_the_song()
    {
        var outside = 0;
        var fills = 0;
        var slid = new int[2];
        var (unevenPlain, unevenWild, sectionCount) = (0, 0, 0);
        var (colour, rare, height, carried) = (new int[2], new int[2], new double[2], new double[2]);
        for (var seed = 0; seed < 200; seed++)
        {
            Assert.All(OpenChordJams.Generate(seed, new(OpenChords: 0)).Sections, section => Assert.NotEqual("open", section.Sound));
            Assert.All(OpenChordJams.Generate(seed, new(OpenChords: 1)).Sections, section => Assert.Equal("open", section.Sound));

            var plain = OpenChordJams.Generate(seed, new(Variety: 0));
            Assert.Equal(new[] { "Verse", "Chorus", "Bridge" }, plain.Sections.Select(section => section.Name));
            Assert.All(plain.Sections, section => Assert.Equal((2, 16), (section.Repeats, section.Chords.Sum(chord => chord.Beats))));

            Assert.DoesNotMatch("major|Lydian|Mixolydian", OpenChordJams.Generate(seed, new(Mood: 0)).Key);
            Assert.DoesNotMatch("minor|Dorian", OpenChordJams.Generate(seed, new(Mood: 1)).Key);

            var changes = 1 + (seed % 3);
            var jam = OpenChordJams.Generate(seed, new(KeyChanges: changes));
            var (tonic, mode) = jam.Key.Split(' ') is [var t, var m] ? (PitchClass(t), m) : throw new InvalidOperationException(jam.Key);
            var intervals = ScaleModes.Intervals(mode switch { "major" => ScaleMode.Ionian, "minor" => ScaleMode.Aeolian, _ => Enum.Parse<ScaleMode>(mode) });
            int KeyOf(string name) => name.Split([" in ", " to "], StringSplitOptions.None) is [_, var key] ? PitchClass(key) : tonic;
            Assert.Equal(changes, jam.Sections.Select(section => KeyOf(section.Name)).Where(key => key != tonic).Distinct().Count());
            Assert.All(jam.Sections, section => Assert.All(section.Chords, chord =>
                Assert.True(chord.Midi.All(midi => intervals.Select(i => (KeyOf(section.Name) + i) % 12).Contains(midi % 12)), $"seed {seed}: {chord.Symbol} outside {section.Name}")));
            var last = KeyOf(jam.Sections[jam.Form[^1]].Name);
            Assert.NotEqual(tonic, last);
            Assert.Equal(last, RootOf(jam.Ending.Symbol));
            // Each change is led in by a one-chord lift on the new key's fifth.
            Assert.Equal(changes, jam.Sections.Count(section => section.Name.StartsWith("Lift to ", StringComparison.Ordinal) && section.Chords.Count == 1));

            // Now and then a change comes early or late (a 6-beat chord beside a 2-beat one), more often
            // the wilder the structure, and the section keeps its length either way.
            foreach (var (settings, count) in new[] { (new ChordJamSettings(), 0), (new ChordJamSettings(Variety: 1), 1) })
            {
                var song = OpenChordJams.Generate(seed, settings);
                var uneven = song.Sections.Count(section => section.Chords.Any(chord => chord.Beats == 6));
                if (count == 0)
                {
                    unevenPlain += uneven;
                    sectionCount += song.Sections.Count;
                    Assert.All(song.Sections, section => Assert.Equal(16, section.Chords.Sum(chord => chord.Beats)));
                }
                else
                {
                    unevenWild += uneven;
                }
            }

            // Strum variety: none at 0; at full, twisted section patterns and chords with fills of their
            // own, every pattern still eight slots of D, U, B and rests, and a chorus still strummed through.
            Assert.All(OpenChordJams.Generate(seed).Sections, section => Assert.All(section.Chords, chord => Assert.Null(chord.Pattern)));
            var restless = OpenChordJams.Generate(seed, new(StrumVariety: 1));
            fills += restless.Sections.Sum(section => section.Chords.Count(chord => chord.Pattern is not null));
            Assert.All(restless.Sections.SelectMany(section => section.Chords.Select(chord => chord.Pattern ?? section.Pattern).Append(section.Pattern)),
                pattern => Assert.Matches("^[DUB-]{8}$", pattern));
            Assert.All(restless.Sections.Where(section => section.Name.StartsWith("Chorus", StringComparison.Ordinal)),
                section => Assert.DoesNotContain('B', string.Concat(section.Chords.Select(chord => chord.Pattern ?? section.Pattern))));

            // Chord changes every two bars, every bar, every half bar; a short song is verse and chorus
            // twice, a long one ends on a double chorus.
            Assert.All(OpenChordJams.Generate(seed, new(ChangeSpeed: 0)).Sections, section => Assert.Equal(32, section.Chords.Sum(chord => chord.Beats)));
            Assert.All(OpenChordJams.Generate(seed, new(ChangeSpeed: 2)).Sections, section => Assert.Equal(8, section.Chords.Sum(chord => chord.Beats)));
            Assert.Equal(4, OpenChordJams.Generate(seed, new(Length: 0)).Form.Count);
            Assert.Equal(7, OpenChordJams.Generate(seed, new(Length: 1)).Form.Count);

            // Sparse strumming strikes at most four times a bar, busy at least six; tempo sits low or high.
            Assert.True(OpenChordJams.Generate(seed, new(Busyness: 0)).Sections[0].Pattern.Count(slot => slot != '-') <= 4);
            Assert.True(OpenChordJams.Generate(seed, new(Busyness: 1)).Sections[0].Pattern.Count(slot => slot != '-') >= 6);
            Assert.InRange(OpenChordJams.Generate(seed, new(Tempo: 0)).Bpm, 50, 76);
            Assert.InRange(OpenChordJams.Generate(seed, new(Tempo: 1)).Bpm, 108, 140);

            // A capo leaves the shapes and moves the sound: every note up by its frets.
            var capo = OpenChordJams.Generate(seed, new(Capo: 3));
            var shapeTonic = PitchClass(capo.Key.Split(' ')[0]);
            var capoScale = ScaleModes.Intervals(ModeOf(capo.Key)).Select(i => (shapeTonic + 3 + i) % 12).ToHashSet();
            if (ModeOf(capo.Key) == ScaleMode.Phrygian)
            {
                capoScale.Add((shapeTonic + 3 + 4) % 12); // the Spanish slide jams' borrowed major third
            }
            Assert.Equal((3, (shapeTonic + 3) % 12), (capo.Capo, PitchClass(capo.SoundingKey.Split(' ')[0])));
            Assert.All(capo.Sections.SelectMany(section => section.Chords), chord => Assert.All(chord.Midi, midi => Assert.Contains(midi % 12, capoScale)));

            // Borrowed chords come from the parallel key and nowhere else.
            var borrowed = OpenChordJams.Generate(seed, new(Borrowed: 1));
            var home = PitchClass(borrowed.Key.Split(' ')[0]);
            var own = ScaleModes.Intervals(ModeOf(borrowed.Key)).Select(i => (home + i) % 12).ToHashSet();
            var other = ScaleModes.Intervals(ModeOf(borrowed.Key) is ScaleMode.Ionian or ScaleMode.Lydian or ScaleMode.Mixolydian ? ScaleMode.Aeolian : ScaleMode.Ionian)
                .Select(i => (home + i) % 12).ToHashSet();
            foreach (var chord in borrowed.Sections.SelectMany(section => section.Chords))
            {
                var notes = chord.Midi.Select(midi => midi % 12).ToHashSet();
                Assert.True(notes.IsSubsetOf(own) || notes.IsSubsetOf(other), $"seed {seed}: {chord.Symbol} is in neither key");
                outside += notes.IsSubsetOf(own) ? 0 : 1;
            }

            // The taste sliders, counted over every song: colour, rare shapes, carried strings, barre height.
            var library = OpenChordJams.Voicings.DistinctBy(Shape).ToDictionary(Shape);
            IEnumerable<OpenChordJams.Voicing> Played(ChordJamSettings settings)
                => OpenChordJams.Generate(seed, settings).Sections.SelectMany(section => section.Chords).Select(chord => library[Shape(chord.Symbol, chord.Frets)]);
            colour[0] += Played(new(Colour: 0)).Count(v => v.HasColour);
            slid[0] += Played(new(SlidChords: 0)).Count(v => v.From is not null);
            slid[1] += Played(new(SlidChords: 1)).Count(v => v.From is not null);
            colour[1] += Played(new(Colour: 1)).Count(v => v.HasColour);
            rare[0] += Played(new(Difficulty: 0)).Count(v => v.Rare);
            rare[1] += Played(new(Difficulty: 1)).Count(v => v.Rare);
            height[0] += Played(new(OpenChords: 0, Position: 0)).Average(v => v.Frets.Where(f => f > 0).Min());
            height[1] += Played(new(OpenChords: 0, Position: 1)).Average(v => v.Frets.Where(f => f > 0).Min());
            static double Carried(List<OpenChordJams.Voicing> chords) => chords.Zip(chords.Skip(1), (a, b) => Enumerable.Range(0, 6).Count(s => a.Frets[s] >= 0 && a.Frets[s] == b.Frets[s])).Average();
            carried[0] += Carried([.. Played(new(Smoothness: 0))]);
            carried[1] += Carried([.. Played(new(Smoothness: 1))]);
        }
        Assert.True(outside > 50, $"only {outside} borrowed chords");
        Assert.True(fills > 200, $"only {fills} fills at full strum variety");
        Assert.InRange(unevenPlain, sectionCount / 20, sectionCount * 2 / 5);
        Assert.True(unevenWild > unevenPlain * 1.5, $"uneven sections {unevenPlain} plain → {unevenWild} wild");
        Assert.Equal(0, slid[0]);
        Assert.True(slid[1] > 400, $"only {slid[1]} slid-shape chords with the slider at the top");
        Assert.True(colour[1] > colour[0] * 1.5, $"colour {colour[0]} → {colour[1]}");
        Assert.True(rare[1] > rare[0] * 2, $"rare {rare[0]} → {rare[1]}");
        // Most roots have one barre shape below the 9th fret, so position only chooses where there are two.
        Assert.True(height[1] > height[0] + 100, $"barre height {height[0] / 200:0.0} → {height[1] / 200:0.0}");
        Assert.True(carried[1] > carried[0] * 1.2, $"carried strings {carried[0] / 200:0.00} → {carried[1] / 200:0.00}");
    }

    private static ScaleMode ModeOf(string key) => key.Split(' ')[1] switch
    {
        "major" => ScaleMode.Ionian,
        "minor" => ScaleMode.Aeolian,
        var mode => Enum.Parse<ScaleMode>(mode),
    };

    private sealed class JamComparer : IEqualityComparer<ChordJamDto>
    {
        public bool Equals(ChordJamDto? a, ChordJamDto? b)
            => a!.Key == b!.Key && a.Bpm == b.Bpm && a.Ending.Symbol == b.Ending.Symbol
            && a.Sections.SelectMany(s => s.Chords.Select(c => s.Pattern + c.Symbol)).SequenceEqual(b.Sections.SelectMany(s => s.Chords.Select(c => s.Pattern + c.Symbol)));

        public int GetHashCode(ChordJamDto jam) => jam.Key.GetHashCode();
    }
}
