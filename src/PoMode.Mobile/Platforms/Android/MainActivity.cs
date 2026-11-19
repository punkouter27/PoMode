using Android.App;
using Android.Content;
using Android.Content.PM;
using PoMode.Mobile.Services;

namespace PoMode.Mobile;

// pomode://song/{jobId} opens a song (the "analysis ready" notification uses it; also an adb test hook).
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = MobileConfig.AppScheme,
    DataHost = "song")]
public class MainActivity : MauiAppCompatActivity
{
}
