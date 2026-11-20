using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.Api.Features.Analysis;
using PoMode.Api.Platform;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using PoMode.Shared.Validation;

namespace PoMode.Api.Features.Auth;

/// <summary>
/// The whole identity model: there is no sign-in and nothing is private. A first visit gets a random
/// guest id in a cookie so it can start straight away; picking three initials then opens the
/// collection those initials own ("initials:ABC"), on this device or any other. Typing someone
/// else's initials opens theirs, which is the point: the initials are the whole key.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/session", (HttpContext context)
                => TypedResults.Ok(SessionOf(context.User)))
            .AllowAnonymous()
            .WithTags("Auth")
            .WithSummary("Who this browser is.");

        // Session:DefaultInitials (empty in every real deployment) lets the browser test suite's fresh
        // guests arrive with initials, so the first-visit prompt does not stand over every page it drives.
        var defaultInitials = app.ServiceProvider.GetRequiredService<IConfiguration>()["Session:DefaultInitials"];
        defaultInitials = InitialsRequestValidator.IsInitials(defaultInitials) ? defaultInitials : null;

        // Idempotent on purpose: the client calls this whenever it finds itself without a session, and
        // a second tab racing the first must not replace a guest (and orphan their library) with another.
        app.MapPost("/auth/guest", async (HttpContext context) =>
            {
                if (PoUser.IdOf(context.User) is not null)
                {
                    return TypedResults.Ok(SessionOf(context.User));
                }

                var principal = GuestPrincipal($"guest:{Guid.NewGuid():N}", defaultInitials);
                await context.SignInAsync(PoAuth.SessionScheme, principal, new AuthenticationProperties { IsPersistent = true });
                return TypedResults.Ok(SessionOf(principal));
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(PoRateLimits.GuestPolicy)
            .WithTags("Auth")
            .WithSummary("Starts a persistent guest session, or returns the current one.");

        // Switches this browser to the initials' collection. Songs it made before it had initials
        // (a "guest:" id) move into that collection with it; switching between initials moves nothing.
        app.MapPut("/auth/initials", async Task<Results<Ok<SessionDto>, ValidationProblem, BadRequest<string>>> (
                InitialsRequest request, IValidator<InitialsRequest> validator, HttpContext context, JobStore store,
                CancellationToken ct) =>
            {
                request.Initials = request.Initials.Trim().ToUpperInvariant();
                var check = await validator.ValidateAsync(request, ct);
                if (!check.IsValid)
                {
                    return TypedResults.ValidationProblem(check.ToDictionary());
                }
                if (PoUser.KindOf(context.User) != SessionKind.Guest || PoUser.IdOf(context.User) is not { } current)
                {
                    return TypedResults.BadRequest("Only a guest session can change its initials.");
                }

                var collection = PoUser.CollectionOf(request.Initials);
                if (current.StartsWith("guest:", StringComparison.Ordinal))
                {
                    await MoveCollectionAsync(store, current, collection, ct);
                }
                var principal = GuestPrincipal(collection, request.Initials);
                await context.SignInAsync(PoAuth.SessionScheme, principal, new AuthenticationProperties { IsPersistent = true });
                return TypedResults.Ok(SessionOf(principal));
            })
            .RequireAuthorization()
            .DisableAntiforgery()
            .WithTags("Auth")
            .WithSummary("Sets the three initials this guest goes by.");

        // Ends this browser's session; the mobile app mints a fresh guest straight after.
        app.MapPost("/auth/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(PoAuth.SessionScheme);
                return TypedResults.Ok(new SessionDto(SessionKind.None, null));
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("Auth")
            .WithSummary("Ends this browser's session.");

        return app;
    }

    /// <summary>Every job <paramref name="from"/> owns, now <paramref name="to"/>'s. A running job
    /// keeps the move: JobStore saves never put an owner back. The demo copy every new browser is given
    /// is dropped rather than moved when the collection already has one, so it is never there twice.</summary>
    public static async Task<int> MoveCollectionAsync(JobStore store, string from, string to, CancellationToken ct)
    {
        var jobs = new List<JobState>();
        foreach (var jobId in store.ListJobIds())
        {
            if (JobId.IsValid(jobId) && await store.LoadAsync(jobId, ct) is { } state)
            {
                jobs.Add(state);
            }
        }
        var hasDemo = jobs.Any(job => job.OwnerId == to && IsDemo(job));

        var moved = 0;
        foreach (var job in jobs.Where(job => job.OwnerId == from))
        {
            if (hasDemo && IsDemo(job))
            {
                store.TryDelete(job.JobId);
            }
            else if (await store.ReassignOwnerAsync(job.JobId, from, to, ct))
            {
                moved++;
            }
        }
        return moved;
    }

    private static bool IsDemo(JobState job) => job.Origin?.Kind == TakeOrigin.Demo;

    private static ClaimsPrincipal GuestPrincipal(string id, string? initials)
    {
        Claim[] name = initials is null ? [] : [new Claim(ClaimTypes.Name, initials)];
        return new ClaimsPrincipal(new ClaimsIdentity(
            [.. name, .. PoUser.Stamp(id, SessionKind.Guest)], PoAuth.SessionScheme));
    }

    private static SessionDto SessionOf(ClaimsPrincipal user)
        => new(PoUser.KindOf(user), PoUser.DisplayNameOf(user));
}
