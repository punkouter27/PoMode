using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using PoMode.Shared.Account;

namespace PoMode.Api.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/session", (HttpContext context)
                => TypedResults.Ok(SessionOf(context.User)))
            .AllowAnonymous()
            .WithTags("Auth")
            .WithSummary("Who the browser is signed in as.");

        // Idempotent on purpose: the client calls this whenever it finds itself signed out, and a
        // second tab racing the first must not replace a guest (and orphan their library) with another.
        app.MapPost("/auth/guest", async (HttpContext context) =>
            {
                if (PoUser.IdOf(context.User) is not null)
                {
                    return TypedResults.Ok(SessionOf(context.User));
                }

                var id = Guid.NewGuid();
                var displayName = $"Guest {(id.GetHashCode() & 0x7FFFFFFF) % 9000 + 1000}";
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, displayName), .. PoUser.Stamp($"guest:{id:N}", SessionKind.Guest)],
                    PoAuth.SessionScheme);
                var principal = new ClaimsPrincipal(identity);
                await context.SignInAsync(PoAuth.SessionScheme, principal,
                    new AuthenticationProperties { IsPersistent = true });
                return TypedResults.Ok(SessionOf(principal));
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("Auth")
            .WithSummary("Starts a persistent guest session, or returns the current one if already signed in.");

        // Ends this browser's session; the client mints a fresh guest straight after.
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

    private static SessionDto SessionOf(ClaimsPrincipal user)
        => new(PoUser.KindOf(user), PoUser.DisplayNameOf(user), PoUser.EmailOf(user));
}
