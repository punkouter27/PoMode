using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.SongStructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongLoop;

/// <summary>
/// The chord cycle a song comes back to most, one chord per bar, so the Mode Lab and Practice can play
/// it under a melody and Hooktheory can be asked which other songs are built on it.
/// </summary>
/// <remarks>
/// Each bar (the same grid the section ribbon is cut on) takes the chord that sounds longest in it,
/// and only when that chord fills at least half the bar. Every four- and eight-bar run is then counted
/// without overlap, so a one-chord vamp does not count itself at every bar it passes. Eight bars win
/// only when they cover more of the song than the best four and are not one four-bar loop said twice.
/// A run that never comes round a second time is not a loop, and the song gets none.
/// </remarks>
public static class SongLoopFinder
{
    public const int MinRepeats = 2;

    /// <summary>A bar's chord: root pitch class, a quality the backing pads can voice, and its name.</summary>
    public readonly record struct BarChord(int Root, string Quality, string Symbol)
    {
        public bool SameChord(BarChord other) => Root == other.Root && Quality == other.Quality;
    }

    public sealed record Loop(IReadOnlyList<BarChord> Chords, int Repeats, ScaleMode Mode, int ParentTonic)
    {
        /// <summary>Each chord as a formula counted from the parent key, as <see cref="SongLoops"/> carries it.</summary>
        public IReadOnlyList<string> Formulas => [.. Chords.Select(chord => SongLoops.Formula(chord.Root - ParentTonic, chord.Quality))];
    }

    public static Loop? Find(IReadOnlyList<ChordSpan> chords, ModalResult result, TempoMapDto? tempoMap, BeatGridDto? beats)
    {
        var duration = chords.Count == 0 ? 0.0 : chords.Max(chord => chord.EndSec);
        if (duration <= 0 || result.PrimaryMode is not { } mode)
        {
            return null;
        }

        var bars = BarChords(chords, SongSectionBuilder.BarStarts(duration, result, tempoMap, beats));
        var four = Best(bars, 4);
        var eight = Best(bars, 8);
        var pick = eight is { } longer
                   && !longer.Chords.Take(4).Zip(longer.Chords.Skip(4)).All(pair => pair.First.SameChord(pair.Second))
                   && longer.Repeats * 8 > (four?.Repeats ?? 0) * 4
            ? eight
            : four;
        if (pick is not { Repeats: >= MinRepeats } found)
        {
            return null;
        }
        return new Loop(StartOnHome(found.Chords, bars, result.TonicPitchClass), found.Repeats, mode,
            ScaleModes.ParentTonic(result.TonicPitchClass, mode));
    }

    /// <summary>The chord that holds each bar, or null where none fills half of it.</summary>
    internal static BarChord?[] BarChords(IReadOnlyList<ChordSpan> chords, double[] edges)
    {
        var bars = new BarChord?[Math.Max(edges.Length - 1, 0)];
        for (var bar = 0; bar < bars.Length; bar++)
        {
            var (start, end) = (edges[bar], edges[bar + 1]);
            var held = chords
                .Where(chord => chord.EndSec > start && chord.StartSec < end && PitchNames.TryParseRoot(chord.Root, out _))
                .GroupBy(chord => (Root: RootOf(chord), Quality: ChordQualities.Normalise(chord.Quality)))
                .Select(group => (group.Key, group.First().Symbol,
                    Seconds: group.Sum(chord => Math.Min(chord.EndSec, end) - Math.Max(chord.StartSec, start))))
                .OrderByDescending(candidate => candidate.Seconds)
                .FirstOrDefault();
            if (held.Seconds >= (end - start) / 2)
            {
                bars[bar] = new BarChord(held.Key.Root, held.Key.Quality, held.Symbol);
            }
        }
        return bars;
    }

    private static int RootOf(ChordSpan chord) => PitchNames.TryParseRoot(chord.Root, out var root) ? root : 0;

    /// <summary>The run of <paramref name="length"/> chords that comes round most often without
    /// overlapping itself; earliest first on a tie.</summary>
    private static (IReadOnlyList<BarChord> Chords, int Repeats)? Best(BarChord?[] bars, int length)
    {
        var runs = new Dictionary<string, (int First, int LastStart, int Count)>();
        for (var start = 0; start + length <= bars.Length; start++)
        {
            var window = bars[start..(start + length)];
            if (window.Any(bar => bar is null))
            {
                continue;
            }
            var key = string.Join(' ', window.Select(bar => $"{bar!.Value.Root}{bar.Value.Quality}"));
            if (!runs.TryGetValue(key, out var run))
            {
                runs[key] = (start, start, 1);
            }
            else if (start >= run.LastStart + length)
            {
                runs[key] = (run.First, start, run.Count + 1);
            }
        }
        if (runs.Count == 0)
        {
            return null;
        }
        var best = runs.Values.OrderByDescending(run => run.Count).ThenBy(run => run.First).First();
        return ([.. bars.Skip(best.First).Take(length).Select(bar => bar!.Value)], best.Count);
    }

    /// <summary>
    /// The same cycle started on the home chord, when the song itself plays it in that order somewhere.
    /// The generator delays the home chord to suit the melody either way; this only makes the loop read
    /// the way a player would count it in.
    /// </summary>
    private static IReadOnlyList<BarChord> StartOnHome(IReadOnlyList<BarChord> loop, BarChord?[] bars, int home)
    {
        var at = loop.ToList().FindIndex(chord => chord.Root == home);
        if (at <= 0)
        {
            return loop;
        }
        var rotated = loop.Skip(at).Concat(loop.Take(at)).ToList();
        for (var start = 0; start + rotated.Count <= bars.Length; start++)
        {
            if (rotated.Select((chord, offset) => bars[start + offset] is { } bar && bar.SameChord(chord)).All(same => same))
            {
                return rotated;
            }
        }
        return loop;
    }
}
