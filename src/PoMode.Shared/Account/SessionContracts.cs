namespace PoMode.Shared.Account;

/// <summary>How the caller is identified. A guest is a persistent browser identity held in a cookie.</summary>
public enum SessionKind
{
    None,
    Guest,
    Test,
}

/// <summary>Who the browser is. For a guest <paramref name="DisplayName"/> is their three initials, or
/// null until they pick some; for a test persona it is the persona's name.</summary>
public sealed record SessionDto(
    SessionKind Kind,
    string? DisplayName);

/// <summary>The body of <c>PUT /auth/initials</c>.</summary>
public sealed class InitialsRequest
{
    public string Initials { get; set; } = "";
}
