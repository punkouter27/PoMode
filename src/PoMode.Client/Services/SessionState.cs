using System.Net.Http.Json;
using PoMode.Shared.Account;
using PoMode.Shared.Serialization;

namespace PoMode.Client.Services;

/// <summary>
/// The browser's session, established once before any page talks to the API. There is no sign-in: a
/// visitor is made a guest on the spot, so every job lands in a library that belongs to someone, and
/// is then asked for three initials, which open that person's collection on any device.
/// </summary>
public sealed class SessionState(HttpClient http)
{
    /// <summary>The source-generated contracts, so the trimmed client never serializes by reflection.</summary>
    private static PoModeJsonContext Json => PoModeJsonContext.Default;

    private Task<SessionDto>? _ready;
    private bool _promptRequested;
    private bool _dismissed;

    public SessionDto? Current { get; private set; }

    /// <summary>A guest with no initials yet (until they say "not now"), or someone who asked to
    /// change theirs.</summary>
    public bool IsPromptOpen
        => Current is { Kind: SessionKind.Guest } guest
           && (_promptRequested || (guest.DisplayName is null && !_dismissed));

    public event Action? Changed;

    /// <summary>Resolves the session, minting a guest if there is none. Shared by every caller, so
    /// two components asking at once cause one round trip rather than two guests.</summary>
    public Task<SessionDto> EnsureAsync() => _ready ??= ResolveAsync();

    /// <summary>Re-queries the server session (for example after switching dev persona).</summary>
    public Task<SessionDto> RefreshAsync() => _ready = ResolveAsync();

    public void OpenPrompt()
    {
        _promptRequested = true;
        Changed?.Invoke();
    }

    /// <summary>"Not now". A guest who has no initials yet is asked again on the next visit.</summary>
    public void ClosePrompt()
    {
        _promptRequested = false;
        _dismissed = true;
        Changed?.Invoke();
    }

    /// <summary>Switches to the initials' collection (taking along anything made before there were
    /// initials). Returns the server's message when it refused them.</summary>
    public async Task<string?> SetInitialsAsync(string initials)
    {
        using var response = await http.PutAsJsonAsync("auth/initials", new InitialsRequest { Initials = initials }, Json.InitialsRequest);
        if (!response.IsSuccessStatusCode)
        {
            // The form runs the same validator first, so this is the server refusing for its own reasons.
            return await ProblemDetailsParser.TryReadAsync(response) is { Detail: { Length: > 0 } detail }
                ? detail
                : "Those initials could not be saved. Try again.";
        }
        Current = await response.Content.ReadFromJsonAsync(Json.SessionDto) ?? Current;
        _ready = Task.FromResult(Current!);
        _promptRequested = false;
        Changed?.Invoke();
        return null;
    }

    private async Task<SessionDto> ResolveAsync()
    {
        var session = await http.GetFromJsonAsync("api/auth/session", Json.SessionDto)
                      ?? new SessionDto(SessionKind.None, null);
        if (session.Kind == SessionKind.None)
        {
            var response = await http.PostAsync("auth/guest", content: null);
            response.EnsureSuccessStatusCode();
            session = await response.Content.ReadFromJsonAsync(Json.SessionDto) ?? session;
        }
        Current = session;
        Changed?.Invoke();
        return session;
    }
}
