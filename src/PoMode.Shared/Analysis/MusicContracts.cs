namespace PoMode.Shared.Analysis;

public sealed record NoteEvent(int MidiPitch, double StartSec, double DurationSec, int Velocity);

public sealed record ChordSpan(string Symbol, string Root, string Quality, double StartSec, double EndSec);

/// <summary>
/// A fast first look at an upload (preview.json), computed from raw chroma and onset energy
/// before the real pipeline stages run. Provisional by definition — the full analysis replaces
/// it — so the client labels it "first look" and gates on <see cref="Confidence"/>.
/// </summary>
public sealed record AnalysisPreviewDto(string TonicName, double TempoBpm, double Confidence);

/// <summary>
/// The song's beat grid for the client metronome: beats fall at
/// <see cref="FirstBeatSec"/> + k·(60/<see cref="Bpm"/>). One tempo covers the whole song;
/// a low <see cref="Confidence"/> means "no usable beats" and the metronome stays unavailable.
///
/// <para>Deliberately still one tempo. <see cref="TempoMapDto"/> describes how the tempo actually
/// moves, but the metronome, the syncopation figure and beat-synchronous chord segmentation all
/// assume a single regular grid, so the map is reported alongside this rather than replacing it.</para>
/// </summary>
/// <param name="Downbeats">
/// Bar starts in seconds, when the beat tracker could hear them (a neural tracker can; the classic
/// onset-autocorrelation one cannot and leaves this null). When present, measure numbers count these
/// instead of assuming four beats from t=0.
/// </param>
/// <param name="Tracker">The executor that produced this grid, or null for a grid that was given rather than measured.</param>
public sealed record BeatGridDto(
    double Bpm,
    double FirstBeatSec,
    double Confidence,
    IReadOnlyList<double>? Downbeats = null,
    string? Tracker = null);

/// <summary>One measure's own tempo, read from the gap to the next downbeat.</summary>
/// <param name="Number">1-based, matching the measure numbers the canvas shows.</param>
/// <param name="Changed">Audibly different from the previous measure — the moment worth showing.</param>
public sealed record TempoMeasureDto(int Number, double StartSec, double Bpm, bool Changed);

/// <summary>
/// How the tempo moves across the song, measure by measure — for music that was played rather than
/// programmed, where a single BPM is an average of something that never actually happened.
///
/// <para><paramref name="IsSteady"/> is the headline: true and the per-measure list is noise around
/// one tempo, false and the list is the interesting part. <paramref name="Measures"/> is empty when
/// no beat grid could be trusted, which the client must show as "unknown" rather than as steady.</para>
/// </summary>
public sealed record TempoMapDto(
    double MedianBpm,
    double MinBpm,
    double MaxBpm,
    bool IsSteady,
    double Confidence,
    IReadOnlyList<TempoMeasureDto> Measures);

/// <summary>
/// A min/max envelope of one track, <c>Peaks[2i]</c> and <c>Peaks[2i+1]</c> being the lowest and highest
/// sample in the i-th equal slice. <paramref name="Source"/> says which track: "vocals" once separation
/// has written the stem, "mix" (the upload itself) before then.
/// </summary>
public sealed record WaveformPeaksDto(double DurationSec, string Source, IReadOnlyList<float> Peaks);

/// <summary>
/// The band's pitch-class energy over time, for drawing under the chord lane: <paramref name="Frames"/>
/// frames of 12 bytes each (0-255), base64 in <paramref name="Energy"/>, row 0 being the song's own
/// tonic so the home note sits at the bottom of the strip. What the chord reading was made from,
/// shown as it was heard.
/// </summary>
/// <summary>One sung line, as Whisper heard it on the separated vocal, with when it was sung.</summary>
public sealed record LyricLineDto(double StartSec, double EndSec, string Text);

/// <summary>A song's chords as a player fingers them (<c>GET /api/analysis/{id}/shapes</c>): the capo
/// fret that puts most of its playing time into open shapes (0 for none), the server's sentence about
/// it, and the chords, longest-held first.</summary>
public sealed record ChordShapesDto(int CapoFret, string Advice, IReadOnlyList<ChordShapeDto> Chords);

/// <summary>One chord: as it sounds (<see cref="Symbol"/>, <see cref="Root"/>) and the shape played
/// behind the capo (<see cref="ShapeSymbol"/>, <see cref="ShapeRoot"/>), with chords-db's suffix and
/// the share of the song it is held for.</summary>
public sealed record ChordShapeDto(string Symbol, string ShapeSymbol, int Root, int ShapeRoot, string Suffix, double Percent);

/// <summary>One open guitar chord jam on the Practice page (<c>GET /api/chord-jams</c>): a progression
/// of open-position voicings, with the sentence saying why it sounds richer than it is to play, and an
/// eighth-note strum pattern over a 4/4 bar (D down, U up, B bass alone, - rest).</summary>
public sealed record ChordJamDto(
    string Id, string Name, string Key, string Feel, double Bpm, string Pattern, IReadOnlyList<JamChordDto> Chords);

/// <summary>One chord of a jam as fingered, low E string first: fret (-1 muted, 0 open) and finger per
/// string, any barred frets, the MIDI notes a strum sounds in standard tuning, and its length in beats.</summary>
public sealed record JamChordDto(string Symbol, int[] Frets, int[] Fingers, int[] Barres, int[] Midi, int Beats);

public sealed record ChromaDto(double FramesPerSecond, int Frames, int TonicPitchClass, string Energy);
