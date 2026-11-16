using System.Text.RegularExpressions;
using PoMode.Shared.Analysis;

namespace PoMode.Api.Features.ChordJams;

/// <summary>
/// Fits a jam's tempo to its words. Each lyric line is sung over one chord, so how comfortable a line
/// is depends on its syllables against how long its chord lasts at the tempo. The tempo slider picks
/// a tempo before there are any words; this moves it, as little as it can, into the range where the
/// typical line sits at 2 to 3.2 syllables a second (relaxed pop phrasing, neither rushed nor
/// dragging), with the busiest lines held under 4.5. Inside that range the slider's tempo stands.
/// </summary>
public static partial class JamTempo
{
    private const double SlowestTypical = 2.0;
    private const double FastestTypical = 3.2;
    private const double FastestBusy = 4.5;

    /// <summary>The tempo to play the song at so its words sit comfortably; the jam's own tempo when
    /// it already does, or when it has no words to fit (all held sounds, say).</summary>
    public static double Singable(ChordJamDto jam, IReadOnlyList<IReadOnlyList<string>> lyrics)
    {
        // Syllables per beat of every sung line. A chord of two beats or less carries one held sound and
        // one of two bars or more a held "oh": neither is phrased against the clock, so only the lines
        // between count.
        var density = new List<double>();
        for (var f = 0; f < jam.Form.Count && f < lyrics.Count; f++)
        {
            var chords = jam.Sections[jam.Form[f]].Chords;
            for (var l = 0; l < lyrics[f].Count; l++)
            {
                var beats = chords[l % chords.Count].Beats;
                var syllables = Syllables(lyrics[f][l]);
                if (beats is > 2 and < 8 && syllables >= 3)
                {
                    density.Add((double)syllables / beats);
                }
            }
        }
        if (density.Count == 0)
        {
            return jam.Bpm;
        }
        density.Sort();
        var typical = density[density.Count / 2];
        var busy = density[(int)Math.Min(density.Count - 1, Math.Ceiling(density.Count * 0.9) - 1)];

        // syllables a second = syllables per beat × beats a second.
        var low = 60 * SlowestTypical / typical;
        var high = Math.Min(60 * FastestTypical / typical, 60 * FastestBusy / busy);
        var bpm = Math.Clamp(jam.Bpm, Math.Min(low, high), high);
        return Math.Clamp(2 * Math.Round(bpm / 2), 50, 160);
    }

    /// <summary>A sung syllable count: vowel groups per word, a silent final e left out ("slowly" 2,
    /// "light" 1, "fire" 1), at least one per word.</summary>
    public static int Syllables(string line)
        => Words().Matches(line.ToLowerInvariant()).Sum(word =>
        {
            var w = word.Value;
            var count = Vowels().Count(w);
            if (w.Length > 2 && w.EndsWith('e') && !w.EndsWith("le", StringComparison.Ordinal) && count > 1)
            {
                count--;
            }
            return Math.Max(1, count);
        });

    [GeneratedRegex("[a-z']+")]
    private static partial Regex Words();

    [GeneratedRegex("[aeiouy]+")]
    private static partial Regex Vowels();
}
