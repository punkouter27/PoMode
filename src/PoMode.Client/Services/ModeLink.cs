using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>
/// A mode carried from a song page to the Mode Lab or Practice: <c>?tonic=2&amp;mode=Dorian</c> is D
/// Dorian. The tonic is the mode's own home note, as an analysis names it; both pages are keyed by
/// the parent key, so reading the link converts it.
/// </summary>
public static class ModeLink
{
    public static string Query(int tonicPitchClass, ScaleMode mode) => $"tonic={tonicPitchClass}&mode={mode}";

    /// <summary>The same link from a page keyed by the parent key: C and Dorian is D Dorian.</summary>
    public static string QueryFromParent(int parentTonic, ScaleMode mode)
        => Query((parentTonic + ScaleModes.ModeDegreeOffset(mode)) % 12, mode);

    /// <summary>The parent key and mode a link asks for, or false when it carries none (or nonsense).</summary>
    public static bool TryRead(int? tonic, string? mode, out int parentTonic, out ScaleMode scaleMode)
    {
        parentTonic = 0;
        scaleMode = default;
        if (tonic is not (>= 0 and < 12) || !Enum.TryParse(mode, ignoreCase: true, out scaleMode) || !Enum.IsDefined(scaleMode))
        {
            return false;
        }
        parentTonic = ScaleModes.ParentTonic(tonic.Value, scaleMode);
        return true;
    }
}
