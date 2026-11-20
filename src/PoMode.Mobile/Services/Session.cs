using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using PoMode.Shared.Serialization;

namespace PoMode.Mobile.Services;

/// <summary>
/// Who the app is. The first launch mints a persistent guest session whose cookie owns the library,
/// and the guest picks three initials to go by (there is no sign-in, as on the web).
/// </summary>
public sealed class Session(MobileApi api)
{
    public SessionDto? Current { get; private set; }

    public event Action? Changed;

    public string Label => Current switch
    {
        { Kind: SessionKind.Guest } s => s.DisplayName ?? "Guest (no initials yet)",
        { Kind: SessionKind.Test } s => s.DisplayName ?? "Test user",
        _ => "No session",
    };

    /// <summary>Makes sure there is an identity: the stored guest cookie, else a fresh guest.</summary>
    public async Task<SessionDto?> EnsureAsync()
    {
        try
        {
            Current = await api.GetSessionAsync();
            if (Current is null || Current.Kind == SessionKind.None)
            {
                Current = await api.StartGuestAsync();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Current = await api.StartGuestAsync();
        }

        Changed?.Invoke();
        return Current;
    }

    /// <summary>Saves the initials; false when the server refused them.</summary>
    public async Task<bool> SetInitialsAsync(string initials)
    {
        if (await api.SetInitialsAsync(initials) is not { } updated)
        {
            return false;
        }
        Current = updated;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ends the guest session: a new guest starts with an empty library.</summary>
    public async Task StartOverAsync()
    {
        await api.LogoutAsync();
        await EnsureAsync();
    }
}

/// <summary>Live progress for one job over /hubs/analysis (JobStatusChanged, QueuePosition), as the web's progress card has it.</summary>
public sealed class JobWatcher(MobileConfig config, CookieContainer cookies) : IAsyncDisposable
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
