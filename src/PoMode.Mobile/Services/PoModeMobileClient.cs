using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Services;

public sealed class PoModeMobileClient(
    HttpClient http,
    MobileConfig config,
    SecureSessionStore sessionStore,
    OfflineTakeQueue queue) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<JobStatusDto>? JobUpdated;

    private Uri BuildUri(string relativePath) => new(new Uri(config.BaseUrl + "/"), relativePath.TrimStart('/'));

    public async Task<SessionDto?> GetSessionAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<SessionDto>(BuildUri("/api/auth/session"), ct).ConfigureAwait(false);

    public async Task<PushConfigDto?> GetPushConfigAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<PushConfigDto>(BuildUri("/api/push"), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LibraryEntryDto>> GetLibraryAsync(CancellationToken ct = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return await queue.LoadLibraryAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var entries = await http.GetFromJsonAsync<List<LibraryEntryDto>>(BuildUri("/api/library"), ct).ConfigureAwait(false) ?? [];
            await queue.SaveLibraryAsync(entries, ct).ConfigureAwait(false);
            return entries;
        }
        catch (HttpRequestException)
        {
            return await queue.LoadLibraryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<JobStatusDto> UploadViaTusAndStartAsync(string filePath, string fileName, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, BuildUri("/api/uploads"));
        createReq.Headers.Add("Tus-Resumable", "1.0.0");
        createReq.Headers.Add("Upload-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var metadata = $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName))}";
        createReq.Headers.Add("Upload-Metadata", metadata);

        using var createResp = await http.SendAsync(createReq, ct).ConfigureAwait(false);
        createResp.EnsureSuccessStatusCode();
        var location = createResp.Headers.Location ?? throw new InvalidOperationException("Missing tus Location header.");
        var uploadUri = location.IsAbsoluteUri ? location : BuildUri(location.OriginalString);

        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, uploadUri)
        {
            Content = new ByteArrayContent(bytes),
        };
        patchReq.Headers.Add("Tus-Resumable", "1.0.0");
        patchReq.Headers.Add("Upload-Offset", "0");
        patchReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");

        using var patchResp = await http.SendAsync(patchReq, ct).ConfigureAwait(false);
        patchResp.EnsureSuccessStatusCode();

        var uploadId = uploadUri.Segments.Last().Trim('/');
        using var startResp = await http.PostAsync(BuildUri($"/api/analysis/uploads/{Uri.EscapeDataString(uploadId)}"), content: null, ct).ConfigureAwait(false);
        startResp.EnsureSuccessStatusCode();
        return await startResp.Content.ReadFromJsonAsync<JobStatusDto>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty JobStatusDto response.");
    }

    public async Task ConnectJobHubAsync(string jobId, CancellationToken ct = default)
    {
        var fakeUser = await sessionStore.GetFakeUserAsync().ConfigureAwait(false);
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(BuildUri("/hubs/analysis"), options =>
                {
                    options.AccessTokenProvider = () => sessionStore.GetBearerTokenAsync();
                    if (!string.IsNullOrWhiteSpace(fakeUser))
                    {
                        options.Headers["X-Fake-User"] = fakeUser;
                    }
                })
                .WithAutomaticReconnect()
                .Build();
            _hub.On<JobStatusDto>("JobStatusChanged", status => JobUpdated?.Invoke(status));
        }

        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync(ct).ConfigureAwait(false);
        }

        await _hub.InvokeAsync("Subscribe", jobId, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
    }
}
