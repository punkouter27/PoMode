namespace PoMode.Shared.Account;

/// <summary>How the caller is identified. A guest is a persistent browser identity held in a cookie.</summary>
public enum SessionKind
{
    None,
    Guest,
    Test,
}

/// <summary>Who the browser is signed in as.
/// <paramref name="Email"/> is populated for a test persona named like an address.</summary>
public sealed record SessionDto(
    SessionKind Kind,
    string? DisplayName,
    string? Email = null);
