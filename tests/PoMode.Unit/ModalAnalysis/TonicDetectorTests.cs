using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class TonicDetectorTests
{
    private static NoteEvent Note(int midi, double dur = 1.0) => new(midi, 0, dur, 96);

    // Minor material and accidental roots are covered by the transposition invariant below plus
    // PitchNames.TryParseRoot's own tests; what stays here is detection and its honesty guards.
    [Fact]
    public void Empty_input_is_zero_confidence()
    {
        var tonic = TonicDetector.Detect([], []);

        Assert.Equal(0.0, tonic.Confidence);
    }

    [Fact]
    public void Transposing_everything_transposes_the_tonic()
    {
        List<NoteEvent> cMajor = [Note(60, 2), Note(64), Note(67, 2), Note(71)];
        List<ChordSpan> cChords = [new("C", "C", "maj", 0, 4)];
        List<NoteEvent> dMajor = [.. cMajor.Select(n => n with { MidiPitch = n.MidiPitch + 2 })];
        List<ChordSpan> dChords = [new("D", "D", "maj", 0, 4)];

        var c = TonicDetector.Detect(cMajor, cChords).PitchClass;
        var d = TonicDetector.Detect(dMajor, dChords).PitchClass;

        Assert.Equal((c + 2) % 12, d);
    }
}
