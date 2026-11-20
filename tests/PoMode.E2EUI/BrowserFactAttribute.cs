using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// A browser test: skipped, with one clear reason, when no Playwright Chromium is installed on this machine
/// (Po web standard: E2E tiers skip cleanly when their prerequisites are absent). Install with
/// <c>pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (!BrowserCheck.ChromiumInstalled)
        {
            Skip = BrowserCheck.SkipReason;
        }
    }
}

/// <summary>Whether Playwright's Chromium is on disk, looked up where Playwright itself installs it.</summary>
public static class BrowserCheck
{
    public const string SkipReason =
        "E2EUI skipped: no Playwright Chromium installed. Run: pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium";

    public static bool ChromiumInstalled { get; } = Find();

    private static bool Find()
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(root) || root == "0")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(home, "Library", "Caches", "ms-playwright")
                    : Path.Combine(home, ".cache", "ms-playwright");
        }

        return Directory.Exists(root)
            && Directory.EnumerateDirectories(root, "chromium*").Any();
    }
}
