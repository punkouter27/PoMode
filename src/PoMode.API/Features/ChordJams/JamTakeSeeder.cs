using PoMode.API.Audio;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordJams;

/// <summary>
/// Prepares a take sung over a guitar jam song for the normal pipeline, the way
/// <c>HumTakeSeeder</c> does for a Mode Lab loop: the take holds one voice, and the harmony it was sung
/// over is known exactly, so the job is handed it rather than left to guess it from a voice.
/// </summary>
/// <remarks>
/// The song arrives whole with the take (the jam the browser played, chord by chord), not as a seed to
/// regenerate: a saved song is kept as it was, and the generator may have changed since it was made.
/// Its form is laid out at the tempo it was played at, from bar one (the browser trims the take to
/// the song's first downbeat), into the job's chord track and a known beat grid. Separation and chord
/// detection are marked provided; pitch tracking and the mode analysis then run as for any take, so
/// the mode the job reports is a reading of the voice over these chords.
/// </remarks>
public sealed class JamTakeSeeder(JobStore store, TimeProvider time)
{
    private const string ProvidedChordsExecutor = "GuitarJamBacking";
    private const string SkippedSeparationExecutor = "SkippedDryVocal";

    /// <summary>As for a hum take: a sliver of a chord at the end is not a window worth scoring.</summary>
    private const double MinSpanSeconds = 0.25;

    /// <summary>Bounds a posted song is held to before anything is laid out from it.</summary>
    private const int MaxSections = 24, MaxFormEntries = 64, MaxChords = 32, MaxRepeats = 8, MaxBeats = 16;

    /// <summary>Why a posted song cannot be laid out, or null when it can.</summary>
    public static string? Problem(ChordJamDto song, double bpm)
    {
        if (bpm is < 30 or > 300)
        {
            return "The tempo must be between 30 and 300 BPM.";
        }
        if (song.Ending is null || song.Sections is not { Count: > 0 and <= MaxSections } || song.Form is not { Count: > 0 and <= MaxFormEntries })
        {
            return "The song has no sections or form to sing over.";
        }
        if (song.Form.Any(index => index < 0 || index >= song.Sections.Count))
        {
            return "The song's form points at a section it does not have.";
        }
        var chords = song.Sections.SelectMany(section => section.Chords).Append(song.Ending).ToList();
        if (song.Sections.Any(section => section.Chords is not { Count: > 0 and <= MaxChords } || section.Repeats is < 1 or > MaxRepeats)
            || chords.Any(chord => chord.Beats is < 1 or > MaxBeats || !PitchNamesParse(chord.Symbol)))
        {
            return "The song has a chord that cannot be read.";
        }
        return null;
    }

    public async Task SeedAsync(JobState state, ChordJamDto song, double bpm, CancellationToken ct)
    {
        var spans = Lay(song, bpm);
        var takeSeconds = AudioDecoder.TryReadDurationSeconds(store.InputPath(state)) ?? spans[^1].EndSec;
        var chords = spans
            .Where(span => span.StartSec < takeSeconds)
            .Select(span => span with { EndSec = Math.Min(span.EndSec, takeSeconds) })
            .Where(span => span.EndSec - span.StartSec > MinSpanSeconds)
            .ToList();
        if (chords.Count == 0)
        {
            return; // nothing sounded under the take: let the job run the ordinary way
        }

        await store.WriteArtifactAsync(state.JobId, "chords.json", chords, ct);
        // Played from bar one at this tempo, and the take is trimmed to that downbeat: known, not estimated.
        await store.WriteArtifactAsync(state.JobId, "beats.json", new BeatGridDto(bpm, 0.0, 1.0), ct);

        var now = time.GetUtcNow();
        state.MarkProvided(StageNames.Separating, SkippedSeparationExecutor, now);
        state.MarkProvided(StageNames.ChordDetecting, ProvidedChordsExecutor, now);
        // Says what was played to the singer, never what they sang: the mode of the voice is the answer
        // the analysis gives, and the song's key stated beside it as the same fact would pre-empt it.
        var title = string.IsNullOrWhiteSpace(song.Title) ? "" : $" “{song.Title}”";
        state.Origin = new TakeOrigin(TakeOrigin.JamTake, $"Sung over a guitar jam{title} in {song.Key} at {bpm:0} BPM",
            JamSong: JamSongs.Fingerprint(song));
    }

    /// <summary>The song's chords in the order they play, timed from bar one: every entry of the form,
    /// every time round, then the final chord.</summary>
    public static List<ChordSpan> Lay(ChordJamDto song, double bpm)
    {
        var secondsPerBeat = 60.0 / bpm;
        var spans = new List<ChordSpan>();
        var at = 0.0;
        void Add(JamChordDto chord)
        {
            var (root, quality) = Parse(chord.Symbol);
            var end = at + (chord.Beats * secondsPerBeat);
            spans.Add(new ChordSpan(chord.Symbol, root, quality, at, end));
            at = end;
        }
        foreach (var index in song.Form)
        {
            var section = song.Sections[index];
            for (var round = 0; round < section.Repeats; round++)
            {
                section.Chords.ToList().ForEach(Add);
            }
        }
        Add(song.Ending);
        return spans;
    }

    /// <summary>
    /// A jam chord's root and the quality the mode engine voices it as. The jam names chords the way a
    /// guitarist reads them (Cadd9, Fmaj7#11/E, A7sus4); the engine knows seven qualities, so each folds
    /// to the nearest: a seventh or ninth on a minor chord is min7, on a major-seventh chord maj7, a
    /// plain 7 or 9 dom7, any other minor min, and the rest (add9, sus, 6, power chords) major, as
    /// <see cref="ChordQualities"/> already folds sus.
    /// </summary>
    public static (string Root, string Quality) Parse(string symbol)
    {
        var chord = symbol.Split('/')[0];
        var length = chord.Length > 1 && chord[1] is '#' or 'b' ? 2 : 1;
        var suffix = chord[length..];
        var quality = suffix switch
        {
            _ when suffix.StartsWith("maj7", StringComparison.Ordinal) || suffix.StartsWith("maj9", StringComparison.Ordinal) => ChordQualities.Major7,
            _ when suffix.StartsWith("m7", StringComparison.Ordinal) || suffix.StartsWith("m9", StringComparison.Ordinal) => ChordQualities.Minor7,
            _ when suffix.StartsWith('m') && !suffix.StartsWith("maj", StringComparison.Ordinal) => ChordQualities.Minor,
            _ when suffix.StartsWith('7') || suffix.StartsWith('9') => ChordQualities.Dominant7,
            _ => ChordQualities.Major,
        };
        return (chord[..length], quality);
    }

    private static bool PitchNamesParse(string? symbol)
        => !string.IsNullOrEmpty(symbol) && symbol.Length <= 24 && symbol[0] is >= 'A' and <= 'G';
}
