using PoMode.Client.Services;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Client;

/// <summary>The links between the song page, the Mode Lab and Practice carry a mode by its own home note,
/// while both pages are keyed by the parent key; a slip here opens the right mode in the wrong key.</summary>
public class ModeLinkTests
{
    [Fact]
    public void A_link_carries_the_home_note_and_opens_on_its_parent_key()
    {
        // D Dorian, as an analysis names it, opens on C: the same seven notes.
        Assert.Equal("tonic=2&mode=Dorian", ModeLink.Query(2, ScaleMode.Dorian));
        Assert.True(ModeLink.TryRead(2, "dorian", out var parent, out var mode));
        Assert.Equal((0, ScaleMode.Dorian), (parent, mode));

        // From a page keyed by the parent and back again: C and Dorian is D Dorian; F# Locrian sits on G.
        Assert.Equal("tonic=2&mode=Dorian", ModeLink.QueryFromParent(0, ScaleMode.Dorian));
        Assert.Equal("tonic=6&mode=Locrian", ModeLink.QueryFromParent(7, ScaleMode.Locrian));
        Assert.True(ModeLink.TryRead(6, "Locrian", out parent, out _));
        Assert.Equal(7, parent);

        // Anything else is no link at all, and the page keeps its own defaults.
        Assert.False(ModeLink.TryRead(null, "Dorian", out _, out _));
        Assert.False(ModeLink.TryRead(12, "Dorian", out _, out _));
        Assert.False(ModeLink.TryRead(2, "Bebop", out _, out _));
        Assert.False(ModeLink.TryRead(2, "42", out _, out _));
    }
}
