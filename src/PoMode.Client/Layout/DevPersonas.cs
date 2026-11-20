namespace PoMode.Client.Layout;

/// <summary>The dev persona switcher on /settings, outside Production: each sends an X-Fake-User
/// header (DevFakeUserHandler), and "Guest" goes back to the browser's own cookie.</summary>
public static class DevPersonas
{
    public sealed record DevPersona(string Label, string? FakeUserHeader);

    public static readonly IReadOnlyList<DevPersona> All =
    [
        new("Guest (Cookie)", null),
        new("Dev User", "dev-user"),
        new("Alice", "alice"),
    ];
}
