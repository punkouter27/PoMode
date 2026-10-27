using System.Numerics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Deterministic modal derivation (spec §6). Pure function over Phase-2 artifacts — no audio, no models.</summary>
public static class ModalAnalysisEngine
{
    private const int MinimumDistinctPitchClasses = 3;
    private const int MaxMatchesPerWindow = 4;
    // Must stay strictly below the smallest possible coverage step (1/12 ≈ 0.0833, the
    // step size when 12 distinct pitch classes are sung) so the bonus can only break ties
    // within a coverage level and can never invert the ordering between coverage levels.
    private const double CharacteristicBonus = 0.05;

    public static ModalResult Analyze(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        double tempoBpm = 120.0,
        bool tempoEstimated = true,
        double tuningOffsetCents = 0.0,
        IReadOnlyList<double>? downbeats = null)
    {
        var bpm = tempoBpm <= 0 ? 120.0 : tempoBpm;
        var tonic = TonicDetector.Detect(notes, chords);
        var secondsPerMeasure = 4.0 * 60.0 / bpm;

        var sortedNotes = notes.OrderBy(note => note.StartSec).ToArray();

        // Two passes. A window that never sounds the note telling two modes apart scores them the same
        // (F Lydian's vamp without its B is equally F Ionian), and broken by enum order alone those
        // ties all voted Ionian and outvoted the windows that did sing the ♯4. So every mode is scored
        // in every window first, and a tie inside a window goes to the mode the whole song fits best.
        var scoredWindows = new List<(ChordSpan Chord, int[] Intervals, int Mask, List<Scored>? Scores)>(chords.Count);
        var songFit = new double[Enum.GetValues<ScaleMode>().Length];
        foreach (var chord in chords)
        {
            var intervals = SungIntervals(sortedNotes, chord, tonic.PitchClass);
            var vocalMask = intervals.Aggregate(0, (mask, interval) => mask | (1 << interval));
            var scores = intervals.Length < MinimumDistinctPitchClasses ? null : ScoreModes(vocalMask, intervals);
            if (scores is not null)
            {
                var weight = Math.Max(chord.EndSec - chord.StartSec, 0.0001);
                foreach (var score in scores)
                {
                    songFit[(int)score.Match.Mode] += score.Match.Confidence * weight;
                }
            }
            scoredWindows.Add((chord, intervals, vocalMask, scores));
        }

        var windows = new List<ModalWindow>(chords.Count);
        for (var index = 0; index < scoredWindows.Count; index++)
        {
            var (chord, intervals, vocalMask, scores) = scoredWindows[index];
            var insufficient = scores is null;
            IReadOnlyList<ModalMatch> matches = scores is null
                ? []
                : scores
                    .OrderByDescending(entry => entry.Match.Confidence)
                    .ThenByDescending(entry => songFit[(int)entry.Match.Mode])
                    .ThenBy(entry => entry.ModeSize)
                    .ThenBy(entry => (int)entry.Match.Mode)
                    .Take(MaxMatchesPerWindow)
                    .Select(entry => entry.Match)
                    .ToArray();

            windows.Add(new ModalWindow(
                Index: index,
                StartSec: chord.StartSec,
                EndSec: chord.EndSec,
                ChordSymbol: chord.Symbol,
                MeasureNumber: MeasureAt(chord.StartSec, secondsPerMeasure, downbeats),
                VocalMask: vocalMask,
                SungIntervals: intervals,
                InsufficientEvidence: insufficient,
                Matches: matches));
        }

        var (primaryMode, primaryConfidence) = PickPrimary(windows);

        return new ModalResult(
            SchemaVersion: 1,
            TonicPitchClass: tonic.PitchClass,
            TonicName: PitchNames.Name(tonic.PitchClass),
            TonicConfidence: tonic.Confidence,
            PrimaryMode: primaryMode,
            PrimaryConfidence: primaryConfidence,
            TempoBpm: bpm,
            TempoEstimated: tempoEstimated,
            Windows: windows,
            TuningOffsetCents: tuningOffsetCents);
    }

    /// <summary>
    /// 1-based measure number at <paramref name="seconds"/>. With heard downbeats it is the count of
    /// bar starts at or before that moment (a pickup before the first one belongs to measure 1);
    /// without them it falls back to 4/4 at the song's tempo from t=0, which is all a grid with no
    /// downbeats can claim.
    /// </summary>
    private static int MeasureAt(double seconds, double secondsPerMeasure, IReadOnlyList<double>? downbeats)
    {
        if (downbeats is not { Count: >= 2 })
        {
            return (int)(seconds / secondsPerMeasure) + 1;
        }
        var started = 0;
        while (started < downbeats.Count && downbeats[started] <= seconds + 1e-6)
        {
            started++;
        }
        return Math.Max(1, started);
    }

    /// <summary>Distinct, ascending intervals above the tonic sung inside the chord's half-open
    /// span. Binary-searches the start-sorted notes so each window only touches its own notes.</summary>
    private static int[] SungIntervals(NoteEvent[] sortedNotes, ChordSpan chord, int tonicPitchClass)
    {
        var seen = new bool[12];
        for (var index = TimelineSearch.LowerBound(sortedNotes, chord.StartSec, static n => n.StartSec);
             index < sortedNotes.Length && sortedNotes[index].StartSec < chord.EndSec;
             index++)
        {
            seen[PitchNames.IntervalAboveTonic(sortedNotes[index].MidiPitch, tonicPitchClass)] = true;
        }

        var intervals = new List<int>(12);
        for (var interval = 0; interval < 12; interval++)
        {
            if (seen[interval])
            {
                intervals.Add(interval);
            }
        }
        return [.. intervals];
    }

    private readonly record struct Scored(ModalMatch Match, int ModeSize);

    /// <summary>Every mode's fit to one window, unordered.</summary>
    private static List<Scored> ScoreModes(int vocalMask, int[] intervals)
    {
        var sungCount = BitOperations.PopCount((uint)vocalMask);
        var scored = new List<Scored>();

        foreach (var mode in ModeDefinitions.All)
        {
            var modeMask = ModeDefinitions.Mask(mode);
            var matched = intervals.Where(i => (modeMask & (1 << i)) != 0).ToArray();
            var outside = intervals.Where(i => (modeMask & (1 << i)) == 0).ToArray();
            var coverage = (double)matched.Length / sungCount;

            var characteristic = ModeDefinitions.CharacteristicIntervals(mode);
            var present = characteristic.Count(i => (vocalMask & (1 << i)) != 0);
            var bonus = characteristic.Count == 0 ? 0 : CharacteristicBonus * present / characteristic.Count;

            var confidence = Math.Clamp(coverage + bonus, 0.0, coverage < 1.0 ? 0.99 : 1.0);
            scored.Add(new Scored(new ModalMatch(mode, confidence, matched, outside), ModeDefinitions.Intervals(mode).Count));
        }
        return scored;
    }

    private static (ScaleMode? Mode, double Confidence) PickPrimary(List<ModalWindow> windows)
    {
        var usable = windows.Where(window => !window.InsufficientEvidence && window.Matches.Count > 0).ToArray();
        if (usable.Length == 0)
        {
            return (null, 0.0);
        }

        var totals = new Dictionary<ScaleMode, double>();
        var allWeight = 0.0;
        foreach (var window in usable)
        {
            var top = window.Matches[0];
            var weight = Math.Max(window.EndSec - window.StartSec, 0.0001);
            totals[top.Mode] = totals.GetValueOrDefault(top.Mode) + top.Confidence * weight;
            allWeight += weight;
        }

        var winner = totals.OrderByDescending(pair => pair.Value).ThenBy(pair => (int)pair.Key).First().Key;

        // How much of the song the pick explains: each window where it is a best fit (a tie counts —
        // a window with no B is as much F Lydian as F Ionian) adds its fit, weighted by its length;
        // a window some other scale wins adds nothing. The mean fit inside only the windows it won
        // read 100% for a song whose windows split three ways.
        // Coverage is read off the masks, since a window keeps only its top four matches and five
        // scales can tie on three sung notes.
        var winnerMask = ModeDefinitions.Mask(winner);
        var explained = 0.0;
        foreach (var window in usable)
        {
            var top = window.Matches[0];
            var covered = BitOperations.PopCount((uint)(window.VocalMask & winnerMask));
            if (covered >= top.MatchedIntervals.Count)
            {
                explained += top.Confidence * Math.Max(window.EndSec - window.StartSec, 0.0001);
            }
        }
        return (winner, explained / allWeight);
    }
}
