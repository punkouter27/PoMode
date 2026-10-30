using PoMode.API.Features.Lyrics;
using PoMode.API.Features.ModalMelodies;
using PoMode.API.Features.SongLoop;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Analysis;

public class SongLoopAndLyricsTests
{
    /// <summary>
    /// D Dorian at 120 BPM (2-second bars): one bar of F, then G Dm C Dm four times. The loop is the
    /// four-bar cycle started on the home chord, counted from the parent key (C), and the id it travels
    /// as plays those chords back in the Mode Lab and names them to Hooktheory.
    /// </summary>
    [Fact]
    public void A_repeated_cycle_becomes_a_loop_the_lab_plays_and_Hooktheory_reads()
    {
        string[] bars = ["F", "G", "Dm", "C", "Dm", "G", "Dm", "C", "Dm", "G", "Dm", "C", "Dm", "G", "Dm", "C", "Dm"];
        var result = new ModalResult(1, 2, "D", 0.9, ScaleMode.Dorian, 1.0, 120.0, false, []);

        var loop = SongLoopFinder.Find(Chords(bars), result, tempoMap: null, beats: null);

        Assert.NotNull(loop);
        Assert.Equal(["ii:min", "I:maj", "ii:min", "V:maj"], loop.Formulas);
        Assert.Equal(["Dm", "C", "Dm", "G"], loop.Chords.Select(chord => chord.Symbol));
        Assert.Equal(4, loop.Repeats);

        var id = SongLoops.Id(loop.Mode, 120, loop.Formulas);
        var generated = new ModalMelodyGenerator().Generate(new ModalMelodyRequest(0, ScaleMode.Dorian, id, 120));
        Assert.Equal(id, generated.ProgressionId);
        Assert.Equal(["C", "Dm", "G"], generated.Chords.Select(chord => chord.Symbol).Distinct().Order());
        Assert.Equal("2,1,2,5", HooktheoryClient.ChordPath(loop.Formulas));

        // A run heard once is not a loop, and an id naming a chord the pads cannot voice is no progression.
        Assert.Null(SongLoopFinder.Find(Chords(bars[..5]), result, null, null));
        Assert.Null(SongLoops.Definition("song:Dorian:120:ii:sus9,V:maj"));
    }

    [Fact]
    public void Lrc_lines_are_timed_until_the_next_stamp()
    {
        const string lrc = """
            [ar:Someone]
            [00:01.00]First line
            [00:04.50][00:20.00]Sung twice
            [00:07.00]
            [00:09.00]Last line
            """;

        var lines = LrclibClient.Parse(lrc, durationSec: 30);

        Assert.Equal(
            [(1.0, 4.5, "First line"), (4.5, 7.0, "Sung twice"), (9.0, 19.0, "Last line"), (20.0, 30.0, "Sung twice")],
            lines.Select(line => (line.StartSec, line.EndSec, line.Text)));
    }

    private static List<ChordSpan> Chords(string[] bars)
        => [.. bars.Select((symbol, bar) => new ChordSpan(
            symbol, symbol.TrimEnd('m'), symbol.EndsWith('m') ? "min" : "maj", bar * 2.0, (bar + 1) * 2.0))];
}
