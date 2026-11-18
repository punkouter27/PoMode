namespace PoMode.Shared.Account;

/// <summary>How the caller is signed in. A guest is a real, persistent identity held in a cookie;
/// it only lacks a second device, which is exactly what Microsoft sign-in adds.</summary>
public enum SessionKind
{
    None,
    Guest,
    Microsoft,
    Test,
}

/// <summary>Who the browser is, and whether Microsoft sign-in is on offer at all on this server.
/// <paramref name="Email"/> is the account's address when the sign-in carries one (Microsoft, a test
/// persona named like an address); the header shows it, never the display name.</summary>
public sealed record SessionDto(
    SessionKind Kind,
    string? DisplayName,
    bool MicrosoftAvailable,
    string? Email = null);
