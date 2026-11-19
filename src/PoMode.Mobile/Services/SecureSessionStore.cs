namespace PoMode.Mobile.Services;

/// <summary>The Microsoft (Entra) token in the Keychain/Keystore. A guest needs nothing here: its cookie is the account (<see cref="CookieJar"/>).</summary>
public sealed class SecureSessionStore
{
    private const string BearerTokenKey = "pomode_bearer_token";
    private readonly ISecureStorage _storage = SecureStorage.Default;

    public async Task<string?> GetBearerTokenAsync()
        => await _storage.GetAsync(BearerTokenKey).ConfigureAwait(false);

    public async Task SetBearerTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _storage.Remove(BearerTokenKey);
            return;
        }

        await _storage.SetAsync(BearerTokenKey, token.Trim()).ConfigureAwait(false);
    }
}
