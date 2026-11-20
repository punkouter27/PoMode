using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PoMode.Api.Infrastructure;

namespace PoMode.Api.Features.Auth;

/// <summary>
/// Public guest cookie sessions in every environment, plus FakeAuth headers in Development and Test
/// for automated API tests and local dev personas.
/// </summary>
public static class PoAuth
{
    public const string SessionScheme = "PoMode.Session";
    private const string SelectorScheme = "PoMode";

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

        auth.AddCookie(SessionScheme, ConfigureSessionCookie);
        builder.Services.AddAuthorization();
    }

    private static void ConfigureSessionCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "PoMode.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // A year, sliding: for a guest this cookie *is* the account, and expiring it deletes a library.
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
}
