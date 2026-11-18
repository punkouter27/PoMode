using System.Text.Json;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Services;

public sealed record PendingAudioTake(string Id, string FileName, string LocalPath, DateTimeOffset CreatedUtc);

public sealed class OfflineTakeQueue : IDisposable
{
    private readonly string _queueDir = Path.Combine(FileSystem.AppDataDirectory, "pomode-takes");
    private readonly string _libraryCachePath = Path.Combine(FileSystem.AppDataDirectory, "pomode-library.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineTakeQueue()
    {
        Directory.CreateDirectory(_queueDir);
    }

    public void Dispose() => _gate.Dispose();

    public async Task<PendingAudioTake> EnqueueFileAsync(string sourcePath, string fileName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var audioCopy = Path.Combine(_queueDir, $"{id}_{Path.GetFileName(fileName)}");
            File.Copy(sourcePath, audioCopy, overwrite: true);
            var take = new PendingAudioTake(id, fileName, audioCopy, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(_queueDir, $"{id}.json"), JsonSerializer.Serialize(take), ct).ConfigureAwait(false);
            return take;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingAudioTake>> ListPendingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = new List<PendingAudioTake>();
            foreach (var meta in Directory.EnumerateFiles(_queueDir, "*.json"))
            {
                var json = await File.ReadAllTextAsync(meta, ct).ConfigureAwait(false);
                var take = JsonSerializer.Deserialize<PendingAudioTake>(json);
                if (take is not null)
                {
                    items.Add(take);
                }
            }

            return [.. items.OrderBy(t => t.CreatedUtc)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var metaPath = Path.Combine(_queueDir, $"{id}.json");
            if (File.Exists(metaPath))
            {
                var take = JsonSerializer.Deserialize<PendingAudioTake>(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false));
                if (take is not null && File.Exists(take.LocalPath))
                {
                    File.Delete(take.LocalPath);
                }

                File.Delete(metaPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveLibraryAsync(IReadOnlyList<LibraryEntryDto> entries, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = _libraryCachePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entries), ct).ConfigureAwait(false);
            File.Move(temp, _libraryCachePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LibraryEntryDto>> LoadLibraryAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_libraryCachePath))
            {
                return [];
            }

            var json = await File.ReadAllTextAsync(_libraryCachePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<LibraryEntryDto>>(json) ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }
}
