using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongIdentity;

/// <summary>
/// What a finished upload is, for the library and the song page, and so its lyrics can be looked up
/// rather than heard: the file's own title and artist tags (ATL.NET), else an AcoustID match. Run by
/// <see cref="JobAftercare"/> and stamped on the job as <see cref="JobState.Identity"/>.
/// </summary>
/// <remarks>
/// Tags win because the person put them there. A file without both a title and an artist is left
/// to the fingerprint: a title alone is usually "Track 01", or the recorder's own name for a memo.
/// </remarks>
public sealed class SongIdentifier(JobStore store, AcoustIdClient acoustId, ILogger<SongIdentifier> logger)
{
    static SongIdentifier()
    {
        // ATL otherwise reports the file name ("input") as the title of an untagged file.
        ATL.Settings.UseFileNameWhenNoTitle = false;
    }

    /// <summary>Identifies the job's upload and stamps the answer on the job; null when nothing said.</summary>
    public async Task<SongIdentityDto?> IdentifyAsync(JobState job, CancellationToken ct)
    {
        var path = store.InputPath(job);
        if (!File.Exists(path))
        {
            return null;
        }
        var identity = FromTags(path) ?? await acoustId.LookupAsync(path, ct);
        if (identity is not null)
        {
            await StampAsync(job.JobId, identity, ct);
            logger.LogInformation("Job {JobId} is {Title} by {Artist}, from its {Source}.", job.JobId, identity.Title, identity.Artist, identity.Source);
        }
        return identity;
    }

    /// <summary>Writes the identity onto the job as it is now, not the copy the caller loaded earlier.</summary>
    public async Task StampAsync(string jobId, SongIdentityDto identity, CancellationToken ct)
    {
        if (await store.LoadAsync(jobId, ct) is { } current)
        {
            current.Identity = identity;
            await store.SaveAsync(current, ct);
        }
    }

    internal static SongIdentityDto? FromTags(string path)
    {
        try
        {
            var track = new ATL.Track(path);
            return Clean(track.Title) is { } title && Clean(track.Artist) is { } artist
                ? new SongIdentityDto(title, artist, Clean(track.Album), "tags")
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Clean(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
