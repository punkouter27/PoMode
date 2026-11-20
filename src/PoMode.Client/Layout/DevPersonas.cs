namespace PoMode.Client.Layout;

/// <summary>
/// Shared dev persona definitions for the header X-Fake-User switcher outside Production (Rule 16).
/// </summary>
public static class AccountMenu
{
    public sealed record DevPersona(string Label, string? FakeUserHeader);

    public static readonly IReadOnlyList<DevPersona> Personas =
    [
        new("Guest (Cookie)", null),
        new("Dev User", "dev-user"),
        new("Alice", "alice"),
    ];
}
