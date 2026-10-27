using PoMode.API.Features.ChordShapes;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

/// <summary>The capo is chosen by playing time in open shapes, and only offered when it pays.</summary>
public class CapoAdvisorTests
{
    private static ChordSpan[] Song(params (string Root, string Quality, string Symbol, double Seconds)[] chords)
    {
        var at = 0.0;
        return [.. chords.Select(c => new ChordSpan(c.Symbol, c.Root, c.Quality, at, at += c.Seconds))];
    }

    [Fact]
    public void A_flat_key_gets_the_lowest_capo_that_opens_it_and_an_open_song_gets_none()
    {
        // Eb Ab Bb Cm: no open shape as written. Capo 1 (D G A Bm) and capo 3 (C F G Am) both open
        // three quarters of it; the lower fret wins the tie. "N" is not a chord to play.
        var flat = CapoAdvisor.Advise(Song(
            ("Eb", "maj", "Eb", 4), ("Ab", "maj", "Ab", 4), ("Bb", "maj", "Bb", 4), ("C", "min", "Cm", 4), ("N", "N", "N", 9)));
        Assert.Equal(1, flat.CapoFret);
        Assert.Equal(["D", "G", "A", "Bm"], flat.Chords.Select(c => c.ShapeSymbol));
        Assert.Equal("minor", flat.Chords.Single(c => c.Symbol == "Cm").Suffix);
        Assert.Equal(25, flat.Chords[0].Percent);
        Assert.StartsWith("Capo 1 and play it as D, G, A and Bm shapes", flat.Advice);
        Assert.Contains("75%", flat.Advice);

        var open = CapoAdvisor.Advise(Song(("G", "maj", "G", 8), ("C", "maj", "C", 4), ("E", "min", "Em", 4)));
        Assert.Equal(0, open.CapoFret);
        Assert.Equal("G", open.Chords[0].ShapeSymbol);
        Assert.Equal("Every chord here is an open shape, so no capo is needed.", open.Advice);
    }
}
