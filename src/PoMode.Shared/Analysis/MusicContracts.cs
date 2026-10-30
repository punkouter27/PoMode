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

/// <summary>One open guitar chord jam on the Practice page (<c>GET /api/chord-jams</c>): a whole song in
/// one key (the key of the shapes the hand plays), its verse, chorus and bridge each a progression, the
/// order they play in (<see cref="Form"/>, indexes into <see cref="Sections"/>), the tonic chord it ends
/// on, the sentence about it, and the capo it is played with and the key it then sounds in (the same
/// as <see cref="Key"/> without one). The MIDI notes are the sounding ones, capo included. Lyrics are
/// words to sing along: for each entry of the form, one line per chord as played there, every time round
/// (an empty line where nothing is sung), and the song's title is its chorus hook. Both are null in a
/// song saved before songs had words. Theme is what the words are about, drawn at random per song.</summary>
public sealed record ChordJamDto(
    string Id, string Key, string Feel, double Bpm, IReadOnlyList<JamSectionDto> Sections, IReadOnlyList<int> Form, JamChordDto Ending,
    int Capo, string SoundingKey, string? Title = null, IReadOnlyList<IReadOnlyList<string>>? Lyrics = null, string? Theme = null);

/// <summary>
/// The Guitar jam's sliders, sent with each request for a new song; the same seed and settings always
/// give the same song. The defaults make a simple song: open chords only, the plain verse–chorus–bridge
/// form, no key change, easy shapes, a little colour, light strumming, a medium-slow tempo, nothing
/// borrowed, no capo.
/// </summary>
/// <param name="OpenChords">Share of sections on open chords, 0–1; the rest barre or power chords.</param>
/// <param name="Variety">How far the form strays from plain, 0–1: pre-chorus, long sections, revoiced returns, repeat counts.</param>
/// <param name="Mood">0 sad (minor, Dorian) to 1 happy (major, Mixolydian, Lydian).</param>
/// <param name="KeyChanges">0–3 key changes, the last chorus first.</param>
/// <param name="Difficulty">0–1: how hard a shape may be (fingers, stretch, barre) and how often a rare one turns up.</param>
/// <param name="Colour">0–1: plain triads to add9, sus, sevenths and the rest.</param>
/// <param name="ChangeSpeed">0 two bars a chord, 1 one bar, 2 half a bar.</param>
/// <param name="Busyness">0–1: sparse strums to constant eighths.</param>
/// <param name="Tempo">0–1: about 60 to about 124 BPM, give or take 16.</param>
/// <param name="Borrowed">0–1: how often a chord comes from the parallel key (♭VI, ♭VII, iv in major).</param>
/// <param name="Smoothness">0–1: how strongly a change keeps strings ringing from the chord before.</param>
/// <param name="Length">0–1: short (verse, chorus twice), as written, or long (a double last chorus).</param>
/// <param name="Position">0–1: where barre and power chords sit, the 1st fret to the 9th.</param>
/// <param name="Capo">0–7: the fret the capo is on.</param>
/// <param name="SlidChords">0–1: how often a chord found by sliding an open shape up the neck is played; 0 never.</param>
/// <param name="StrumVariety">0–1: how often a section twists its strum pattern and a chord takes a fill of its own.</param>
public sealed record ChordJamSettings(
    double OpenChords = 1, double Variety = 0, double Mood = 0.5, int KeyChanges = 0,
    double Difficulty = 0.2, double Colour = 0.3, int ChangeSpeed = 1, double Busyness = 0.3,
    double Tempo = 0.4, double Borrowed = 0, double Smoothness = 0.7, double Length = 0.5,
    double Position = 0, int Capo = 0, double StrumVariety = 0, double SlidChords = 0.3);

/// <summary>One section of a jam song: its name (Verse, Chorus, Bridge), its progression as numerals,
/// an eighth-note strum pattern over a 4/4 bar (D down, U up, B bass alone, - rest), how many times
/// it goes round each time it comes up, its chords, and what they are: <c>open</c> chords, <c>barre</c>
/// chords or <c>power</c> chords.</summary>
public sealed record JamSectionDto(string Name, string Numerals, string Pattern, int Repeats, IReadOnlyList<JamChordDto> Chords, string Sound);

/// <summary>One chord of a jam as fingered, low E string first: fret (-1 muted, 0 open) and finger per
/// string, any barred frets, the MIDI notes a strum sounds in standard tuning, its length in beats, and
/// its own strum pattern when it plays a fill rather than its section's.</summary>
public sealed record JamChordDto(string Symbol, int[] Frets, int[] Fingers, int[] Barres, int[] Midi, int Beats, string? Pattern = null);

public sealed record ChromaDto(double FramesPerSecond, int Frames, int TonicPitchClass, string Energy);
