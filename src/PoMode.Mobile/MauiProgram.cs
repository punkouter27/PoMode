using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
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
            .ConfigureLifecycleEvents(ForwardLinks)
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileConfig>();
        builder.Services.AddSingleton(new CookieContainer());
        builder.Services.AddSingleton<CookieJar>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = new HttpClientHandler { CookieContainer = sp.GetRequiredService<CookieContainer>(), UseCookies = true };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoMode.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<MobileApi>();
        builder.Services.AddSingleton<Session>();
        builder.Services.AddSingleton<Uploads>();
        builder.Services.AddSingleton<ToneMixer>();
        builder.Services.AddTransient<JobWatcher>();
#if ANDROID
        builder.Services.AddSingleton<IMicrophone, AndroidMicrophone>();
        builder.Services.AddSingleton<ITrackPlayerFactory, AndroidTrackPlayerFactory>();
        builder.Services.AddSingleton<IAudioOut, AndroidAudioOut>();
        builder.Services.AddSingleton<IToneSession, AndroidToneSession>();
        builder.Services.AddSingleton<IMidiInput, AndroidMidiInput>();
        builder.Services.AddSingleton<IBackgroundWork, AndroidBackgroundWork>();
        builder.Services.AddSingleton<INotifier, AndroidNotifier>();
#elif IOS
        builder.Services.AddSingleton<IMicrophone, IosMicrophone>();
        builder.Services.AddSingleton<ITrackPlayerFactory, IosTrackPlayerFactory>();
        builder.Services.AddSingleton<IAudioOut, IosAudioOut>();
        builder.Services.AddSingleton<IToneSession, IosToneSession>();
        builder.Services.AddSingleton<IMidiInput, IosMidiInput>();
        builder.Services.AddSingleton<IBackgroundWork, IosBackgroundWork>();
        builder.Services.AddSingleton<INotifier, IosNotifier>();
        UserNotifications.UNUserNotificationCenter.Current.Delegate = new IosNotifier.Delegate();
#endif

        builder.Services.AddTransient<LibraryPage>();
        builder.Services.AddTransient<RecordSongPage>();
        builder.Services.AddTransient<SongPage>();
        builder.Services.AddTransient<PracticePage>();
        builder.Services.AddTransient<ModeLabPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<DiagPage>();

        MauiApp app = builder.Build();
        app.Services.GetRequiredService<CookieJar>().RestoreAsync().GetAwaiter().GetResult();

        // Songs waiting to go up go as soon as there is a connection again, and on every start (item 4).
        Uploads uploads = app.Services.GetRequiredService<Uploads>();
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                uploads.Kick();
            }
        };
        _ = Task.Run(async () =>
        {
            if ((await uploads.ListAsync()).Any(u => u.State is not (UploadState.Done or UploadState.Failed)))
            {
                MainThread.BeginInvokeOnMainThread(uploads.Kick);
            }
        });
        return app;
    }

    private static void ForwardLinks(ILifecycleBuilder events)
    {
#if ANDROID
        events.AddAndroid(android => android
            .OnCreate((activity, _) => Forward(activity.Intent))
            .OnNewIntent((_, intent) => Forward(intent)));

        static void Forward(Android.Content.Intent? intent)
        {
            if (intent?.Action == Android.Content.Intent.ActionView && intent.DataString is { } data && Uri.TryCreate(data, UriKind.Absolute, out var uri))
            {
                Application.Current?.SendOnAppLinkRequestReceived(uri);
            }
        }
#endif
    }
}
