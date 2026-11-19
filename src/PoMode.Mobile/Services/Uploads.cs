using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Services;

public enum UploadState
{
    Waiting,
    Uploading,
    Analysing,
    Done,
    Failed,
}

/// <summary>One song on its way: the copy on the phone, how far the tus upload got, and the job it became.</summary>
public sealed record PendingUpload(
    string Id,
    string FileName,
    string LocalPath,
    long Size,
    DateTimeOffset CreatedUtc,
    UploadState State = UploadState.Waiting,
    string? TusUrl = null,
    long Offset = 0,
    string? JobId = null,
    string? Status = null,
    string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<PendingUpload>))]
[JsonSerializable(typeof(List<LibraryEntryDto>))]
internal sealed partial class MobileJsonContext : JsonSerializerContext;

/// <summary>
/// Item 4: a song picked on the phone goes up in 4 MB tus chunks (the web's protocol, POST /api/uploads), resuming from
/// the server's offset after a dropped connection or an app restart, inside a background-work session so the screen
/// can lock. It then waits for the analysis and posts a notification that opens the song when it is done.
/// </summary>
public sealed class Uploads(HttpClient http, ApiClient api, IBackgroundWork background, INotifier notifier) : IDisposable
{
    private const int ChunkBytes = 4 * 1024 * 1024;
    private static readonly string Folder = Path.Combine(FileSystem.AppDataDirectory, "uploads");
    private static readonly string StatePath = Path.Combine(Folder, "queue.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _running = new(1, 1);
    private List<PendingUpload>? _items;

    public event Action? Changed;

    /// <summary>Per-stage model picks (Settings), sent with every new analysis like the web's.</summary>
    public static Dictionary<string, string> ExecutorChoices()
    {
        Dictionary<string, string> picks = [];
        foreach (var (stage, _) in StageNames.ExecutorQueryKeys)
        {
            if (Preferences.Default.Get($"pomode_exec_{stage}", "") is { Length: > 0 } name)
            {
                picks[stage] = name;
            }
        }

        return picks;
    }

    public async Task<IReadOnlyList<PendingUpload>> ListAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return [.. await LoadAsync()];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Copies the picked file into the app (the picker's copy may vanish) and starts working the queue.</summary>
    public async Task EnqueueAsync(FileResult picked)
    {
        Directory.CreateDirectory(Folder);
        string id = Guid.NewGuid().ToString("N");
        string local = Path.Combine(Folder, $"{id}{Path.GetExtension(picked.FileName)}");
        await using (Stream source = await picked.OpenReadAsync())
        await using (FileStream target = File.Create(local))
        {
            await source.CopyToAsync(target);
        }

        await MutateAsync(list => list.Add(new PendingUpload(id, picked.FileName, local, new FileInfo(local).Length, DateTimeOffset.UtcNow)));
        _ = notifier.RequestAsync();
        Kick();
    }

    public Task RemoveAsync(string id) => MutateAsync(list =>
    {
        if (list.Find(u => u.Id == id) is { } item)
        {
            TryDelete(item.LocalPath);
            list.Remove(item);
        }
    });

    public Task ClearFinishedAsync() => MutateAsync(list => list.RemoveAll(u => u.State is UploadState.Done));

    public Task RetryAsync(string id) => MutateAsync(list => Replace(list, id, u => u with { State = UploadState.Waiting, Error = null }));

    /// <summary>Starts the background worker if anything is left to do (app start, back online, a new file).</summary>
    public void Kick()
    {
        if (_running.CurrentCount == 0)
        {
            return;
        }

        background.Start(RunAsync, "Uploading songs");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                PendingUpload? next = (await ListAsync()).FirstOrDefault(u => u.State is UploadState.Waiting or UploadState.Uploading or UploadState.Analysing);
                if (next is null)
                {
                    break;
                }

                if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                {
                    break; // MauiProgram kicks the queue again when the connection is back.
                }

                try
                {
                    await WorkAsync(next, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    // A dropped connection: leave it where it got to; the next kick resumes from the server's offset.
                    await UpdateAsync(next.Id, u => u with { Status = "Waiting for a connection" });
                    break;
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task WorkAsync(PendingUpload item, CancellationToken ct)
    {
        if (item.State is UploadState.Waiting or UploadState.Uploading)
        {
            if (!File.Exists(item.LocalPath))
            {
                await UpdateAsync(item.Id, u => u with { State = UploadState.Failed, Error = "The copy on this phone is gone." });
                return;
            }

            item = await UploadAsync(item, ct);
            if (item.State is UploadState.Failed)
            {
                return;
            }

            var (status, error) = await api.StartFromUploadAsync(item.TusUrl!.Split('/')[^1], ExecutorChoices(), ct);
            if (status is null)
            {
                await UpdateAsync(item.Id, u => u with { State = UploadState.Failed, Error = error });
                return;
            }

            TryDelete(item.LocalPath);
            item = await UpdateAsync(item.Id, u => u with { State = UploadState.Analysing, JobId = status.JobId, Status = "Queued for analysis" });
        }

        // Wait for the analysis. Polling (not the hub) because it has to work with the app in the background.
        while (!ct.IsCancellationRequested)
        {
            JobStatusDto? job = await api.GetStatusAsync(item.JobId!, ct);
            if (job is null)
            {
                await UpdateAsync(item.Id, u => u with { State = UploadState.Failed, Error = "The analysis is gone." });
                return;
            }

            string status = job.Stage.IsTerminal()
                ? job.Stage.ToString()
                : string.Create(CultureInfo.InvariantCulture, $"{StageLabel(job.Stage)} · {job.Progress:P0}");
            background.Update($"{item.FileName}: {status}");
            item = await UpdateAsync(item.Id, u => u with { Status = status });
            if (job.Stage.IsTerminal())
            {
                bool ok = job.Stage == JobStage.Complete;
                await UpdateAsync(item.Id, u => u with { State = ok ? UploadState.Done : UploadState.Failed, Error = ok ? null : job.Error ?? job.Stage.ToString() });
                notifier.Show(
                    ok ? "Analysis ready" : "Analysis stopped",
                    ok ? $"{item.FileName} — tap to see its mode." : $"{item.FileName}: {job.Error ?? job.Stage.ToString()}",
                    new Uri($"{MobileConfig.AppScheme}://song/{item.JobId}"));
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task<PendingUpload> UploadAsync(PendingUpload item, CancellationToken ct)
    {
        if (item.TusUrl is null)
        {
            using HttpRequestMessage create = new(HttpMethod.Post, api.Url("api/uploads"));
            create.Headers.Add("Tus-Resumable", "1.0.0");
            create.Headers.Add("Upload-Length", item.Size.ToString(CultureInfo.InvariantCulture));
            create.Headers.Add("Upload-Metadata",
                $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(item.FileName))},filetype {Convert.ToBase64String(Encoding.UTF8.GetBytes(MimeOf(item.FileName)))}");
            using HttpResponseMessage created = await http.SendAsync(create, ct);
            if (!created.IsSuccessStatusCode || created.Headers.Location is null)
            {
                string reason = await created.Content.ReadAsStringAsync(ct);
                return await UpdateAsync(item.Id, u => u with { State = UploadState.Failed, Error = string.IsNullOrWhiteSpace(reason) ? $"The server refused the file ({(int)created.StatusCode})." : reason });
            }

            Uri location = created.Headers.Location.IsAbsoluteUri ? created.Headers.Location : api.Url(created.Headers.Location.OriginalString);
            item = await UpdateAsync(item.Id, u => u with { State = UploadState.Uploading, TusUrl = location.ToString(), Offset = 0 });
        }
        else
        {
            // Resume: the server says how much it has.
            using HttpRequestMessage head = new(HttpMethod.Head, item.TusUrl);
            head.Headers.Add("Tus-Resumable", "1.0.0");
            using HttpResponseMessage at = await http.SendAsync(head, ct);
            if (at.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // Expired on the server: start over.
                item = await UpdateAsync(item.Id, u => u with { TusUrl = null, Offset = 0 });
                return await UploadAsync(item, ct);
            }

            at.EnsureSuccessStatusCode();
            long offset = long.Parse(at.Headers.GetValues("Upload-Offset").First(), CultureInfo.InvariantCulture);
            item = await UpdateAsync(item.Id, u => u with { Offset = offset });
        }

        await using FileStream file = File.OpenRead(item.LocalPath);
        byte[] chunk = new byte[ChunkBytes];
        while (item.Offset < item.Size)
        {
            file.Position = item.Offset;
            int read = await file.ReadAsync(chunk, ct);
            using HttpRequestMessage patch = new(HttpMethod.Patch, item.TusUrl) { Content = new ByteArrayContent(chunk, 0, read) };
            patch.Headers.Add("Tus-Resumable", "1.0.0");
            patch.Headers.Add("Upload-Offset", item.Offset.ToString(CultureInfo.InvariantCulture));
            patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
            using HttpResponseMessage sent = await http.SendAsync(patch, ct);
            sent.EnsureSuccessStatusCode();
            long offset = long.Parse(sent.Headers.GetValues("Upload-Offset").First(), CultureInfo.InvariantCulture);
            string status = string.Create(CultureInfo.InvariantCulture, $"Uploading {offset * 100 / Math.Max(1, item.Size)}%");
            background.Update($"{item.FileName}: {status}");
            item = await UpdateAsync(item.Id, u => u with { Offset = offset, Status = status });
        }

        return item;
    }

    private static string StageLabel(JobStage stage) => stage switch
    {
        JobStage.Uploaded => "Waiting",
        JobStage.Separating => "Separating the voice",
        JobStage.PitchTracking or JobStage.AwaitingClient => "Reading the melody",
        JobStage.ChordDetecting => "Finding the chords",
        JobStage.ModalAnalysis => "Working out the mode",
        _ => stage.ToString(),
    };

    private static string MimeOf(string name) => Path.GetExtension(name).ToUpperInvariant() switch
    {
        ".MP3" => "audio/mpeg",
        ".WAV" => "audio/wav",
        ".M4A" => "audio/mp4",
        ".FLAC" => "audio/flac",
        _ => "application/octet-stream",
    };

    private static void Replace(List<PendingUpload> list, string id, Func<PendingUpload, PendingUpload> change)
    {
        int i = list.FindIndex(u => u.Id == id);
        if (i >= 0)
        {
            list[i] = change(list[i]);
        }
    }

    private async Task<PendingUpload> UpdateAsync(string id, Func<PendingUpload, PendingUpload> change)
    {
        PendingUpload? result = null;
        await MutateAsync(list =>
        {
            Replace(list, id, change);
            result = list.Find(u => u.Id == id);
        });
        return result ?? throw new InvalidOperationException("The upload was removed.");
    }

    private async Task MutateAsync(Action<List<PendingUpload>> change)
    {
        await _gate.WaitAsync();
        try
        {
            List<PendingUpload> list = await LoadAsync();
            change(list);
            Directory.CreateDirectory(Folder);
            string temp = StatePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(list, MobileJsonContext.Default.ListPendingUpload));
            File.Move(temp, StatePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }

    private async Task<List<PendingUpload>> LoadAsync()
    {
        if (_items is null)
        {
            _items = File.Exists(StatePath)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(StatePath), MobileJsonContext.Default.ListPendingUpload) ?? []
                : [];
        }

        return _items;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _running.Dispose();
    }
}

/// <summary>The library as last seen, so the Library tab opens offline.</summary>
public static class LibraryCache
{
    private static readonly string Path_ = Path.Combine(FileSystem.AppDataDirectory, "library.json");

    public static async Task SaveAsync(List<LibraryEntryDto> entries)
        => await File.WriteAllTextAsync(Path_, JsonSerializer.Serialize(entries, MobileJsonContext.Default.ListLibraryEntryDto));

    public static async Task<List<LibraryEntryDto>> LoadAsync()
        => File.Exists(Path_) ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path_), MobileJsonContext.Default.ListLibraryEntryDto) ?? [] : [];
}
