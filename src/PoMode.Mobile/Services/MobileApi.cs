using System.Net.Http.Json;
using PoMode.ApiClient;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using PoMode.Shared.Serialization;

namespace PoMode.Mobile.Services;

/// <summary>
/// The shared <see cref="AnalysisClient"/>, pointed at the configured server, plus what only the phone
/// needs: a session whose cookie is kept in SecureStorage, and downloads saved to the cache folder.
/// </summary>
public sealed class MobileApi(HttpClient http, MobileConfig config, CookieJar cookies) : AnalysisClient(http)
{
    private static PoModeJsonContext Json => PoModeJsonContext.Default;

    public override Uri Url(string relative) => new(new Uri(config.BaseUrl + "/"), relative.TrimStart('/'));

    // ---- Session ---------------------------------------------------------------------------------

    public Task<SessionDto?> GetSessionAsync(CancellationToken ct = default)
        => Http.GetFromJsonAsync(Url("api/auth/session"), Json.SessionDto, ct);

    /// <summary>A persistent guest: the session cookie is the identity, kept in SecureStorage.</summary>
    public async Task<SessionDto?> StartGuestAsync(CancellationToken ct = default)
    {
        using var response = await Http.PostAsync(Url("auth/guest"), null, ct);
        response.EnsureSuccessStatusCode();
        await cookies.SaveAsync();
        return await response.Content.ReadFromJsonAsync(Json.SessionDto, ct);
    }

    /// <summary>Switches to the collection three initials open; the server re-issues the cookie.</summary>
    public async Task<SessionDto?> SetInitialsAsync(string initials, CancellationToken ct = default)
    {
        using var response = await Http.PutAsJsonAsync(Url("auth/initials"), new InitialsRequest { Initials = initials }, Json.InitialsRequest, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        await cookies.SaveAsync();
        return await response.Content.ReadFromJsonAsync(Json.SessionDto, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        using var _ = await Http.PostAsync(Url("auth/logout"), null, ct);
        cookies.Clear();
    }

    // ---- Downloads -------------------------------------------------------------------------------

    /// <summary>A stem ("vocals", "instrumental", "mix") or export, saved under the cache folder for playback or sharing.</summary>
    public async Task<string?> DownloadAsync(string relative, string fileName, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(Url(relative), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await using (var file = File.Create(path))
        {
            await response.Content.CopyToAsync(file, ct);
        }
        return path;
    }

    /// <summary>The server-synthesized melody + chords (44.1 kHz WAV), saved for the player.</summary>
    public Task<string?> DownloadBackingWavAsync(ModalMelodyRequest backing, CancellationToken ct = default)
        => DownloadAsync($"api/modal-melodies/wav?{BackingQuery(backing)}", $"backing-{backing.Mode}-{backing.TonicPitchClass}-{backing.Seed}.wav", ct);
}
