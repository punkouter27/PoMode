using System.Text.Json;
using System.Text.Json.Serialization;
using PoMode.Shared.Analysis;

namespace PoMode.Api.Features.ChordJams;

/// <summary>
/// The chord jams' written-out tables, kept as data in <c>open-chord-jams.json</c> (embedded in the
/// assembly) rather than as a thousand lines of C# literals: the open and rare voicings, the slide
/// jams, and the keys with their progression shapes. <see cref="OpenChordJams"/> holds the rules.
/// </summary>
internal sealed record JamData(List<JamData.VoicingRow> Voicings, List<JamData.SlideRow> Slides, List<JamData.KeyRow> Keys)
{
    private const string ResourceName = "PoMode.Api.Features.ChordJams.open-chord-jams.json";

    /// <summary>One fingered chord: frets and fingers low E string first, -1 for a muted string.</summary>
    internal sealed record VoicingRow(string Symbol, int Root, int[] Frets, int[] Fingers, bool Rare = false, bool Slid = false);

    internal sealed record SectionRow(string Numerals, string[] Chords);

    /// <summary>A slide jam: its key, the chord that closes it, its sections, and the move in words.</summary>
    internal sealed record SlideRow(string Key, string Home, List<SectionRow> Sections, string Feel);

    /// <summary>A key and mode: how often it comes up, its tonics (pitch classes), and its verse or
    /// chorus shapes and bridge shapes as scale degrees (0 is the tonic).</summary>
    internal sealed record KeyRow(ScaleMode Mode, double Weight, int[] Tonics, int[][] Shapes, int[][] Bridges);

    public static JamData Load()
    {
        using var stream = typeof(JamData).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        return JsonSerializer.Deserialize(stream, JamDataJsonContext.Default.JamData)
            ?? throw new InvalidOperationException($"{ResourceName} is empty.");
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JamData))]
internal sealed partial class JamDataJsonContext : JsonSerializerContext;
