using System.Net.Http.Headers;

namespace PoMode.Mobile.Services;

/// <summary>
/// Adds the Entra bearer token when signed in with Microsoft. A guest rides on the API's PoMode.Session cookie
/// instead (the HttpClientHandler's CookieContainer), exactly like the web.
/// </summary>
public sealed class AuthDelegatingHandler(SecureSessionStore sessionStore) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await sessionStore.GetBearerTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
