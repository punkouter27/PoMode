using System.Globalization;

namespace PoMode.Shared.Analysis;

/// <summary>
/// Which guitar jam song a take was sung over, as a short stable name both sides compute alike: a
/// 64-bit FNV-1a hash of what the singer heard (key, capo, form, every section's chords as fingered and
/// timed, the last chord). A song made again from its seed and settings, reopened from the saved list
/// or played at another tempo is the same song; any change to its chords is another one.
/// </summary>
public static class JamSongs
{
    public static string Fingerprint(ChordJamDto song)
    {
        var parts = new List<string> { song.Key, song.Capo.ToString(CultureInfo.InvariantCulture), string.Join(",", song.Form) };
        parts.AddRange(song.Sections.Select(section => section.Name + ":" + string.Join(" ",
            section.Chords.Select(chord => $"{chord.Symbol}/{string.Join(".", chord.Frets)}/{chord.Beats}"))));
        parts.Add(song.Ending.Symbol);
        var hash = 14695981039346656037UL;
        foreach (var c in string.Join("|", parts))
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
