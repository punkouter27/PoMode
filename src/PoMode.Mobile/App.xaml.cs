using PoMode.Mobile.Services;

namespace PoMode.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new AppShell());

    /// <summary>pomode://song/{jobId} — a finished-analysis notification (item 4) opens the song.</summary>
    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        base.OnAppLinkRequestReceived(uri);
        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Scheme != MobileConfig.AppScheme || uri.Host != "song" || parts.Length != 1)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            for (int i = 0; Shell.Current is null && i < 50; i++)
            {
                await Task.Delay(100);
            }

            if (Shell.Current is { } shell)
            {
                await shell.GoToAsync($"song?id={Uri.EscapeDataString(parts[0])}");
            }
        });
    }
}
