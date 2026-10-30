using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// Turns the detected chord spans into a start-sorted synth-pad note list for the client mixer's
/// "Synth chords" layer. The chord-symbol → pitches decision is musical, so it lives here rather
/// than in mixer.js; the register and voicings match the MIDI export's chord track.
/// </summary>
public static class ChordPadBuilder
{
    /// <summary>Octave 3 root — low enough to sit under the melody. The MIDI export's chord track
    /// reads this same constant, so the two renderings can never drift apart in register.</summary>
    public const int OctaveOffset = 48;

    /// <summary>One chord velocity for both the mixer pad and the MIDI export's chord track.</summary>
    public const int PadVelocity = 72;

    public static IReadOnlyList<NoteEvent> Build(IReadOnlyList<ChordSpan> chords)
    {
        var notes = new List<NoteEvent>(chords.Count * 3);
        foreach (var chord in chords)
        {
            // "N" (no chord) spans have no parsable root and stay silent.
            if (!PitchNames.TryParseRoot(chord.Root, out var rootClass))
            {
                continue;
            }
            var duration = Math.Max(chord.EndSec - chord.StartSec, 0.05);
            foreach (var interval in VoicingFor(chord.Quality))
            {
                notes.Add(new NoteEvent(
                    Math.Clamp(OctaveOffset + rootClass + interval, 0, 127),
                    chord.StartSec,
                    duration,
                    PadVelocity));
            }
        }
        return notes;
    }

    /// <summary>Chord quality → semitone intervals above the root. Shared with the MIDI export.</summary>
    public static int[] VoicingFor(string quality) => ChordQualities.Normalise(quality) switch
    {
        ChordQualities.Minor7 => [0, 3, 7, 10],
        ChordQualities.Major7 => [0, 4, 7, 11],
        ChordQualities.Dominant7 => [0, 4, 7, 10],
        ChordQualities.Minor => [0, 3, 7],
        ChordQualities.Diminished => [0, 3, 6],
        ChordQualities.Augmented => [0, 4, 8],
        _ => [0, 4, 7],
    };
}
