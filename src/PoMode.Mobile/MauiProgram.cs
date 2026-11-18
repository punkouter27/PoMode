using Microsoft.Extensions.Logging;
using PoMode.Mobile.Pages;
using PoMode.Mobile.Services;

namespace PoMode.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileConfig>();
        builder.Services.AddSingleton<SecureSessionStore>();
        builder.Services.AddSingleton<EntraAuthService>();
        builder.Services.AddSingleton<OfflineTakeQueue>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler();
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoMode.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<PoModeMobileClient>();

        builder.Services.AddTransient<CaptureStudioPage>();
        builder.Services.AddTransient<LibraryPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
