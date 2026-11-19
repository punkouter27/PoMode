using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using PoMode.Shared.Serialization;

namespace PoMode.Mobile.Services;

/// <summary>
/// Who the app is. Like the web, nobody has to sign in: the first launch mints a persistent guest (its cookie is the
/// account). Microsoft sign-in (Settings) moves to a bearer token so the same library follows the person to the web.
/// </summary>
public sealed class Session(ApiClient api, SecureSessionStore tokens, EntraAuthService entra)
{
    public SessionDto? Current { get; private set; }

    public event Action? Changed;

    public string Label => Current switch
    {
        { Kind: SessionKind.Microsoft } s => s.Email ?? s.DisplayName ?? "Microsoft account",
        { Kind: SessionKind.Guest } s => s.DisplayName ?? "Guest",
        { Kind: SessionKind.Test } s => s.Email ?? s.DisplayName ?? "Test user",
        _ => "Not signed in",
    };

    /// <summary>Makes sure there is an identity: the stored one, else a fresh guest. A lapsed Microsoft token falls back to it too.</summary>
    public async Task<SessionDto?> EnsureAsync()
    {
        try
        {
            Current = await api.GetSessionAsync();
            if (Current is null || Current.Kind == SessionKind.None)
            {
                if (await tokens.GetBearerTokenAsync() is { Length: > 0 })
                {
                    // The id token lasted an hour and is gone: drop it rather than act as nobody.
                    await tokens.SetBearerTokenAsync(null);
                    Current = await api.GetSessionAsync();
                }

                if (Current is null || Current.Kind == SessionKind.None)
                {
                    Current = await api.StartGuestAsync();
                }
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            await tokens.SetBearerTokenAsync(null);
            Current = await api.StartGuestAsync();
        }

        Changed?.Invoke();
        return Current;
    }

    public async Task SignInWithMicrosoftAsync()
    {
        await entra.SignInWithPkceAsync();
        await EnsureAsync();
    }

    /// <summary>Back to the guest on this phone (the guest cookie is kept — it is the guest's library).</summary>
    public async Task SignOutMicrosoftAsync()
    {
        await entra.SignOutAsync();
        await EnsureAsync();
    }

    /// <summary>Ends the guest too: a new guest starts with an empty library.</summary>
    public async Task StartOverAsync()
    {
        await entra.SignOutAsync();
        await api.LogoutAsync();
        await EnsureAsync();
    }
}

/// <summary>Live progress for one job over /hubs/analysis (JobStatusChanged, QueuePosition), as the web's progress card has it.</summary>
public sealed class JobWatcher(MobileConfig config, SecureSessionStore tokens, CookieContainer cookies) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<JobStatusDto>? Updated;

    public event Action<string, int>? QueuePosition;

    public async Task WatchAsync(string jobId)
    {
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(config.BaseUrl + "/"), "hubs/analysis"), options =>
                {
                    options.AccessTokenProvider = async () => await tokens.GetBearerTokenAsync();
                    options.Cookies = cookies;
                })
                .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, PoModeJsonContext.Default))
                .WithAutomaticReconnect()
                .Build();
            _hub.On<JobStatusDto>("JobStatusChanged", s => Updated?.Invoke(s));
            _hub.On<string, int>("QueuePosition", (id, ahead) => QueuePosition?.Invoke(id, ahead));
        }

        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync();
        }

        await _hub.InvokeAsync("Subscribe", jobId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }
}
