using System.Security.Claims;
using PoMode.Shared.Account;

namespace PoMode.Api.Features.Auth;

/// <summary>
/// "Who is calling?" in one place. Every sign-in path stamps the same two claims — a guest cookie
/// when it is minted, Microsoft when its token is validated, FakeAuth per request — so nothing
/// downstream has to know which of the three it is looking at, or how Entra spells an object id.
/// </summary>
public static class PoUser
{
    public const string IdClaim = "po_uid";
    public const string KindClaim = "po_kind";

    /// <summary>The stable owner key for jobs and push subscriptions, or null when nobody is signed in.
    /// Prefixed by kind ("guest:", "ms:", "test:") so two schemes can never mint the same id.</summary>
    public static string? IdOf(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true && user.FindFirstValue(IdClaim) is { Length: > 0 } id
            ? id
            : null;

    public static SessionKind KindOf(ClaimsPrincipal user)
        => IdOf(user) is null
            ? SessionKind.None
            : Enum.TryParse<SessionKind>(user.FindFirstValue(KindClaim), ignoreCase: true, out var kind)
                ? kind
                : SessionKind.None;

    public static string? DisplayNameOf(ClaimsPrincipal user)
        => user.Identity?.Name ?? user.FindFirstValue("name");

    /// <summary>The account's email: Entra's preferred_username (an address for every Microsoft
    /// account), the email claim, or a name that is itself an address. Null for a guest.</summary>
    public static string? EmailOf(ClaimsPrincipal user)
    {
        if (IdOf(user) is null || KindOf(user) == SessionKind.Guest)
        {
            return null;
        }
        var candidate = user.FindFirstValue("preferred_username")
                        ?? user.FindFirstValue(ClaimTypes.Email)
                        ?? user.FindFirstValue("email")
                        ?? DisplayNameOf(user);
        return candidate is { } address && address.Contains('@', StringComparison.Ordinal) ? address : null;
    }

    public static Claim[] Stamp(string id, SessionKind kind)
        => [new(IdClaim, id), new(KindClaim, kind.ToString())];
}
