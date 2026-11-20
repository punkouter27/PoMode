using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PoMode.Api.Platform;

namespace PoMode.Api.Features.Auth;

/// <summary>
/// Guest cookie sessions in every environment, plus FakeAuth headers in Development and Test for the
/// automated API tests and the local dev personas.
/// </summary>
public static class PoAuth
{
    public const string SessionScheme = "PoMode.Session";
    private const string SelectorScheme = "PoMode";

    /// <summary>What the cookie was called before it took the <c>__Host-</c> name. A browser still
    /// holding it is moved across once (<see cref="UseLegacySessionCookie"/>) so its library survives.</summary>
    private const string LegacyCookieName = "PoMode.Session";

    public static void AddPoAuthentication(this WebApplicationBuilder builder)
    {
        var environment = builder.Environment;
        var allowFakeHeaders = environment.IsDevelopment() || environment.IsEnvironment("Test");

        var auth = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = SelectorScheme;
            options.DefaultChallengeScheme = SelectorScheme;
        });

        if (allowFakeHeaders)
        {
            auth.AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(FakeAuthHandler.UserHeader)
                        ? FakeAuthHandler.SchemeName
                        : SessionScheme);
            auth.AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
        }
        else
        {
            auth.AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
                options.ForwardDefaultSelector = _ => SessionScheme);
        }

        // Secure everywhere a person could be: only the local http:// runs and the test host go without.
        auth.AddCookie(SessionScheme, options => ConfigureSessionCookie(options, secure: !allowFakeHeaders));
        builder.Services.AddAuthorization();
    }

    private static void ConfigureSessionCookie(CookieAuthenticationOptions options, bool secure)
    {
        options.Cookie.Name = PoPlatform.SessionCookieName(secure);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        // A year, sliding: this cookie *is* the guest's identity, and expiring it loses a library.
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
        // This is an API behind a SPA. A redirect to a login page would arrive at fetch() as a 200
        // of HTML; a status code is what the client can act on.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Moves a session from the old cookie name to the current one. The ticket is protected per scheme,
    /// not per cookie name, so the same value is valid under the new name: this request reads it, and
    /// the response renames it. Remove once the old cookies have aged out (a year from 2026-11-20).
    /// </summary>
    public static IApplicationBuilder UseLegacySessionCookie(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            var options = context.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(SessionScheme);
            var current = options.Cookie.Name!;
            var cookies = context.Request.Cookies;
            if (current != LegacyCookieName
                && cookies.TryGetValue(LegacyCookieName, out var legacy)
                && !cookies.ContainsKey(current))
            {
                context.Request.Headers.Cookie = $"{context.Request.Headers.Cookie}; {current}={legacy}";
                var renamed = options.Cookie.Build(context);
                renamed.Expires = DateTimeOffset.UtcNow + options.ExpireTimeSpan;
                context.Response.Cookies.Append(current, legacy, renamed);
                context.Response.Cookies.Delete(LegacyCookieName);
            }
            return next(context);
        });
}
