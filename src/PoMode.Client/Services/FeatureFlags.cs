namespace PoMode.Client.Services;

/// <summary>
/// The extra tools a page shows only once someone switches them on in Settings, off by default so a
/// first visit carries the essentials. Shared like <see cref="ThemeState"/>: the header's menu
/// listens, so turning Diagnostics on in Settings shows its menu item without a reload. Each flag
/// hides one control in place; none swaps a page for a second arrangement (docs/removed.md).
/// </summary>
public sealed class FeatureFlags(BrowserPrefs prefs)
{
    public const string Key = "pm-advanced-tools";

    private Task? _load;

    public AdvancedTools Tools { get; private set; } = new();

    public event Action? Changed;

    /// <summary>Reads the browser once per session; later calls await the same read.</summary>
    public Task LoadAsync() => _load ??= LoadCoreAsync();

    public async Task SetAsync(AdvancedTools tools)
    {
        Tools = tools;
        await prefs.SetAsync(Key, tools, PrefsJsonContext.Default.AdvancedTools);
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        Tools = await prefs.GetAsync(Key, PrefsJsonContext.Default.AdvancedTools) ?? new();
        Changed?.Invoke();
    }
}

/// <summary>Which extra tools are on. A flag added later reads as off from an older saved value.</summary>
public sealed record AdvancedTools(
    bool WindowInspector = false, bool Rerun = false, bool Midi = false, bool Diagnostics = false,
    bool Purity = false, bool Describe = false);
