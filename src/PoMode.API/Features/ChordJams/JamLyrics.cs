using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordJams;

/// <summary>
/// Words to sing over a jam song, so the hands have a voice to keep time with: one line for each chord
/// as it is played, made with the song from its seed, so a saved or replayed song sings the same.
/// </summary>
/// <remarks>
/// The lines are the app's own, put together from small banks of phrase openings and rhyming endings,
/// not taken from any song. Each song draws a theme at random (love, the road, rain, home, summer) and
/// sings in its words. A verse rhymes ABAB and says something new each time it comes round; the
/// chorus is the same every time, a hook line (which names the song) between rhymed lines; a
/// pre-chorus climbs; a bridge asks. The mood slider picks the theme's sad or happy side: below 0.4
/// sad, above 0.6 happy, a mix between. A chord of two beats or less gets a single sung sound ("oh",
/// "yeah"), a chord of two bars or more a held "oh" on the end, and a lift chord before a key change no
/// words.
/// </remarks>
public static class JamLyrics
{
    /// <summary>One theme's words: openings that end on "the", line endings that rhyme in families (each
    /// a place or a thing to follow "the" after any opening), and hook lines, each on a happy and a sad side.</summary>
    private sealed record Theme(
        string Name, string[] HappyStarts, string[] SadStarts, string[][] Rhymes, string[] HappyHooks, string[] SadHooks);

    private static readonly Theme[] Themes =
    [
        new("love",
            ["We were dancing in the", "Holding hands beneath the", "I fell for you in the", "You were laughing by the"],
            ["I still hear you in the", "Your old coat is by the", "Every promise in the", "I keep waiting by the"],
            [["night", "light", "moonlight"], ["dance", "chance", "romance"], ["door", "shore"]],
            ["You and me, you and me", "Hold me close tonight", "Oh, we're alright tonight"],
            ["Only you, only you", "Still I'm holding on", "Say goodbye, say goodbye"]),
        new("the road",
            ["Headlights cutting through the", "Radio is singing to the", "Rolling on along the", "Windows open to the"],
            ["Another mile along the", "Looking back across the", "Nothing left but the", "Walking slowly down the"],
            [["road", "glow", "snow"], ["town", "ground", "sound"], ["night", "light", "twilight"]],
            ["Here we go, here we go", "Windows down, turn it up", "Drive on, drive on"],
            ["Miles away, miles away", "Drive all night", "Don't look back"]),
        new("rain",
            ["Running laughing through the", "Puddles shining in the", "Sing it loud across the", "Dancing barefoot in the"],
            ["Drops are falling on the", "Cold and waiting in the", "Lonely voices in the", "Grey light settles on the"],
            [["rain", "lane", "windowpane"], ["day", "bay", "way"], ["dark", "spark", "park"]],
            ["Let it pour, let it pour", "Dancing in the rain", "Here it comes again"],
            ["Let it rain, let it rain", "Washed away, washed away", "Grey again"]),
        new("home",
            ["Kettle singing by the", "Children laughing on the", "Every window lights the", "Coming back along the"],
            ["Old photographs beside the", "Empty chairs around the", "Dust is settling on the", "I keep waiting by the"],
            [["door", "floor", "shore"], ["fire", "choir"], ["lights", "nights", "heights"]],
            ["Take me home, take me home", "This is where I belong", "Leave the porch light on"],
            ["Can't go home again", "Leave a light on", "Nobody's home"]),
        new("summer",
            ["Barefoot running on the", "Sunlight spilling on the", "Hand in hand along the", "Nothing's better than the"],
            ["Footprints fading in the", "Walking slowly by the", "Nothing left but the", "Cold hands reaching for the"],
            [["sea", "breeze", "trees"], ["sand", "band", "land"], ["sun", "run"]],
            ["Summer's here, summer's here", "Turn it up, we're alive", "Shine on, shine on"],
            ["Summer's gone, summer's gone", "Say goodbye, say goodbye", "Let it go, let it go"]),
    ];

    private static readonly string[] PreStarts = ["And it's rising like the", "I can feel it in the", "Here it comes across the", "Closer, closer to the"];
    private static readonly string[] BridgeStarts = ["What if we could change the", "Maybe it was just the", "Tell me, can you feel the", "If I let go of the"];
    private static readonly string[] HappyShort = ["oh", "yeah", "hey", "oh-oh"];
    private static readonly string[] SadShort = ["oh", "mm", "ah"];

    /// <summary>The song's title (its chorus hook), its theme, and, for each entry of its form, one line
    /// per chord played there, every time round: as many lines as the entry's section has chords times
    /// its repeats.</summary>
    public static (string Title, string Theme, List<List<string>> Lyrics) Write(int seed, double mood, ChordJamDto jam)
    {
        var random = new Random(unchecked((seed * 7919) + 13));
        var theme = Pick(random, Themes);
        bool Sad() => mood < 0.4 || (mood <= 0.6 && random.NextDouble() < 0.5);
        var hook = Pick(random, Sad() ? theme.SadHooks : theme.HappyHooks);
        List<string>? chorus = null;
        var lyrics = new List<List<string>>();
        foreach (var index in jam.Form)
        {
            var section = jam.Sections[index];
            var beats = Enumerable.Range(0, section.Repeats).SelectMany(_ => section.Chords.Select(chord => chord.Beats)).ToList();
            var kind = section.Name.Split(' ')[0];
            if (kind == "Lift")
            {
                lyrics.Add([.. beats.Select(_ => "")]);
                continue;
            }
            // The chorus is sung the same every time, in whatever key and however long.
            if (kind == "Chorus" && chorus is not null && chorus.Count == beats.Count)
            {
                lyrics.Add(chorus);
                continue;
            }
            var lines = Stanza(random, theme, kind, beats, hook, Sad);
            if (kind == "Chorus")
            {
                chorus = lines;
            }
            lyrics.Add(lines);
        }
        return (hook, theme.Name, lyrics);
    }

    private static List<string> Stanza(Random random, Theme theme, string kind, List<int> beats, string hook, Func<bool> sad)
    {
        var a = Pick(random, theme.Rhymes);
        var b = Pick(random, theme.Rhymes.Where(family => family != a).ToArray());
        var lines = new List<string>();
        for (var i = 0; i < beats.Count; i++)
        {
            var mournful = sad();
            if (beats[i] <= 2)
            {
                lines.Add(Pick(random, mournful ? SadShort : HappyShort));
                continue;
            }
            var opening = kind switch
            {
                "Pre-chorus" => PreStarts,
                "Bridge" => BridgeStarts,
                _ => mournful ? theme.SadStarts : theme.HappyStarts,
            };
            var line = kind == "Chorus" && i % 2 == 0 ? hook : $"{Pick(random, opening)} {Rhyme(i % 2 == 0 ? a : b, i / 2)}";
            lines.Add(beats[i] >= 8 ? $"{line}, oh" : line);
        }
        return lines;
    }

    private static string Rhyme(string[] family, int nth) => family[nth % family.Length];

    private static T Pick<T>(Random random, T[] items) => items[random.Next(items.Length)];
}
