using PoMode.Api.Features.Auth;

namespace PoMode.Api.Features.Analysis;

/// <summary>Job and batch ids are 32 lowercase hex chars and are concatenated into file paths,
/// so validity is a security boundary, not a formality.</summary>
public static class JobId
{
    public static bool IsValid(string? id)
        => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);
}

/// <summary>
/// Every route with a {jobId} goes through this. A malformed id answers 404 before any handler runs.
/// Nothing is private, so reading a job (GET) is open to anyone with its link: a song page can be
/// sent to a friend. Changing one (cancel, delete, rerun, a browser handing back its work) is for the
/// collection that owns it, and anyone else gets the same 404 as a missing job. A job from before
/// ownership existed (no owner) can be changed by anyone.
/// </summary>
public sealed class JobAccessFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var route = http.Request.RouteValues;
        if (route.TryGetValue("batchId", out var batchId) && !JobId.IsValid(batchId as string))
        {
            return TypedResults.NotFound();
        }
        if (route.TryGetValue("jobId", out var raw))
        {
            if (raw is not string jobId || !JobId.IsValid(jobId))
            {
                return TypedResults.NotFound();
            }
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && !await CanChangeAsync(http.RequestServices.GetRequiredService<JobStore>(), jobId, PoUser.IdOf(http.User), http.RequestAborted))
            {
                return TypedResults.NotFound();
            }
        }
        return await next(context);
    }

    public static async Task<bool> CanChangeAsync(JobStore store, string jobId, string? caller, CancellationToken ct)
        => JobId.IsValid(jobId)
           && await store.LoadAsync(jobId, ct) is { } state
           && (state.OwnerId is null || state.OwnerId == caller);
}
