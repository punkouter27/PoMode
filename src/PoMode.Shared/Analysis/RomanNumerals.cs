namespace PoMode.Shared.Analysis;

/// <summary>
/// Roman numerals read two ways at once: semitones above the key, and which diatonic letter the degree
/// falls on (the letter is what spells G's flat five as D flat rather than C sharp). The progression
/// presets, a song's loop and the generator all read them here.
/// </summary>
public static class RomanNumerals
{
    private static readonly (string Name, int Semitones, int LetterStep)[] Table =
    [
        ("I", 0, 0), ("bII", 1, 1), ("II", 2, 1), ("bIII", 3, 2), ("III", 4, 2), ("IV", 5, 3),
        ("#IV", 6, 3), ("bV", 6, 4), ("V", 7, 4), ("bVI", 8, 5), ("VI", 9, 5), ("bVII", 10, 6), ("VII", 11, 6),
    ];

    /// <summary>The numeral for a number of semitones above the key; the tritone is Lydian's #IV.</summary>
    public static string Name(int semitones, string quality)
    {
        var name = Table.First(entry => entry.Semitones == ((semitones % 12) + 12) % 12).Name;
        return ChordQualities.IsLowerCase(quality) ? name.ToLowerInvariant() : name;
    }

    public static bool TryRead(string numeral, out int semitones, out int letterStep)
    {
        foreach (var entry in Table)
        {
            if (string.Equals(entry.Name, numeral, StringComparison.OrdinalIgnoreCase))
            {
                (semitones, letterStep) = (entry.Semitones, entry.LetterStep);
                return true;
            }
        }
        (semitones, letterStep) = (0, 0);
        return false;
    }
}
