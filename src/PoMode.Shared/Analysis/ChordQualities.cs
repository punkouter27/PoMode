namespace PoMode.Shared.Analysis;

/// <summary>
/// The chord qualities the app voices, and the one place any other name for one (a recogniser's
/// <c>7</c> or <c>m7</c>, ChordMini's sus chords) is folded onto them. Sus and anything unnamed
/// counts as major, which is how the backing pads have always voiced it.
/// </summary>
public static class ChordQualities
{
    public const string Major = "maj";
    public const string Minor = "min";
    public const string Dominant7 = "dom7";
    public const string Minor7 = "min7";
    public const string Major7 = "maj7";
    public const string Diminished = "dim";
    public const string Augmented = "aug";

    public static IReadOnlyList<string> All { get; } = [Major, Minor, Dominant7, Minor7, Major7, Diminished, Augmented];

    public static string Normalise(string? quality) => quality?.ToLowerInvariant() switch
    {
        "min" or "m" => Minor,
        "min7" or "m7" => Minor7,
        "7" or "dom7" => Dominant7,
        "maj7" => Major7,
        "dim" => Diminished,
        "aug" => Augmented,
        _ => Major,
    };

    /// <summary>What follows the root in a chord's name: Dm, G7, Fmaj7.</summary>
    public static string Suffix(string? quality) => Normalise(quality) switch
    {
        Minor => "m",
        Minor7 => "m7",
        Major7 => "maj7",
        Dominant7 => "7",
        Diminished => "dim",
        Augmented => "aug",
        _ => "",
    };

    /// <summary>Written as a lower-case numeral: ii, vi, vii°.</summary>
    public static bool IsLowerCase(string? quality) => Normalise(quality) is Minor or Minor7 or Diminished;
}
