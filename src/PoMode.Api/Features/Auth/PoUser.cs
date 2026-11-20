using System.Security.Claims;
using PoMode.Shared.Account;
using PoMode.Shared.Validation;

namespace PoMode.Api.Features.Auth;

/// <summary>
/// "Who is calling?" in one place. Both identity paths stamp the same two claims — a guest cookie
/// when it is minted and FakeAuth per request in Development/Test — so nothing downstream has to
/// know which it is looking at.
/// </summary>
public static class PoUser
{
    public const string IdClaim = "po_uid";
    public const string KindClaim = "po_kind";

    /// <summary>The stable owner key for jobs and push subscriptions, or null when nobody is identified.
    /// "initials:ABC" for anyone who picked initials (the same letters open the same collection on any
    /// device), "guest:&lt;guid&gt;" for a browser that has not yet, "test:&lt;name&gt;" for a dev persona.
    /// The prefixes keep the schemes from ever minting the same id.</summary>
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

    /// <summary>What the header shows: a guest's initials (null until they pick some, which is what
    /// opens the prompt; a guest minted before initials existed is "Guest 1234" and is asked too),
    /// or a test persona's name.</summary>
    public static string? DisplayNameOf(ClaimsPrincipal user)
        => KindOf(user) switch
        {
            SessionKind.Guest => user.Identity?.Name is { } name && InitialsRequestValidator.IsInitials(name) ? name : null,
            SessionKind.Test => user.Identity?.Name,
            _ => null,
        };

    /// <summary>The owner key of the collection three initials open.</summary>
    public static string CollectionOf(string initials) => $"initials:{initials}";

    public static Claim[] Stamp(string id, SessionKind kind)
        => [new(IdClaim, id), new(KindClaim, kind.ToString())];
}
