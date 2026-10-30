using System.Globalization;

namespace PoMode.Shared.Analysis;

/// <summary>
/// A song's own chord loop carried as a progression id, <c>song:Dorian:96:ii:min,V:maj</c>: the mode
/// it was heard in, its tempo, and one chord per bar counted from the parent key like the pop presets.
/// The id is the whole definition, so the Mode Lab, Practice and a hum take regenerate the exact
/// backing from it with no store behind it, as they do from a preset's name.
/// </summary>
/// <remarks>
/// Rooted on the parent key rather than the mode's home note on purpose: picking another mode card
/// then keeps the song's real chords and moves only the melody's home note, which is the lab's
/// one-harmony-under-every-mode lesson applied to the person's own song.
/// </remarks>
public static class SongLoops
{
    public const string Prefix = "song:";

    /// <summary>Two four-bar phrases; a loop longer than that is a section, not a loop.</summary>
    public const int MaxChords = 8;

    /// <summary>A chord as a formula: <c>ii:min</c> is a minor chord two semitones above the key.</summary>
    public static string Formula(int semitonesAboveKey, string quality)
        => $"{RomanNumerals.Name(semitonesAboveKey, quality)}:{quality}";

    /// <summary>Reads a formula back, or false for anything the generator could not voice.</summary>
    public static bool TryReadFormula(string formula, out int semitonesAboveKey, out string quality)
    {
        (semitonesAboveKey, quality) = (0, "");
        if (formula.Split(':') is not [var numeral, var named] || !ChordQualities.All.Contains(named)
            || !RomanNumerals.TryRead(numeral, out semitonesAboveKey, out _))
        {
            return false;
        }
        quality = named;
        return true;
    }

    public static string Id(ScaleMode mode, double bpm, IReadOnlyList<string> formulas)
        => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{mode}:{Math.Round(bpm):0}:{string.Join(',', formulas)}");

    /// <summary>The progression a song-loop id describes, or null when the id is not one or is malformed.</summary>
    public static ChordProgressionDefinition? Definition(string? id)
    {
        if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal)
            || id[Prefix.Length..].Split(':', 3) is not [var modeName, var bpmText, var chordList]
            || !Enum.TryParse<ScaleMode>(modeName, out var mode) || !Enum.IsDefined(mode)
            || !int.TryParse(bpmText, NumberStyles.None, CultureInfo.InvariantCulture, out var bpm) || bpm is < 40 or > 240)
        {
            return null;
        }
        var formulas = chordList.Split(',');
        if (formulas.Length > MaxChords || !formulas.All(formula => TryReadFormula(formula, out _, out _)))
        {
            return null;
        }
        var numerals = string.Join(" - ", formulas.Select(formula => formula[..formula.IndexOf(':')]));
        return new ChordProgressionDefinition(
            Id: id,
            Name: $"This song's loop ({numerals})",
            Category: "From your song",
            RomanNumerals: numerals,
            ChordFormulas: formulas,
            Description: "The chords the song repeats most, one per bar, at the song's own tempo.",
            SuggestedMode: mode,
            DefaultBpm: bpm,
            RootsOn: HarmonicRoot.ParentTonic);
    }
}

/// <summary>
/// The chord cycle a song repeats most (<c>GET /api/analysis/{id}/loop</c>): the progression id that
/// carries it to the Mode Lab and Practice, its chords as the song names them, and how many times it
/// came round. <see cref="CanFindSongs"/> says whether the server can look up other songs built on it.
/// </summary>
public sealed record SongLoopDto(string ProgressionId, IReadOnlyList<string> Symbols, string Numerals, int Repeats, bool CanFindSongs);

/// <summary>A published song whose section uses the same chords, from Hooktheory's TheoryTab.</summary>
public sealed record ProgressionSongDto(string Artist, string Song, string Section, string Url);
