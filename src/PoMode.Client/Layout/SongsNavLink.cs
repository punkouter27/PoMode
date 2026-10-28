using Microsoft.AspNetCore.Components.Routing;

namespace PoMode.Client.Layout;

/// <summary>
/// The header's Songs tab. A plain NavLink to "" lights only on the list itself, so a song page, the
/// place a listener spends most of their time, lit no tab at all. This one also owns every /song/ address.
/// </summary>
public sealed class SongsNavLink : NavLink
{
    protected override bool ShouldMatch(string uriAbsolute)
    {
        var path = new Uri(uriAbsolute).AbsolutePath.Trim('/');
        return path.Length == 0 || path.StartsWith("song/", StringComparison.Ordinal);
    }
}
