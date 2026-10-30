using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;
using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>
/// What this browser remembers between visits, all optional: one typed read and write over
/// localStorage. Every page used to hand-roll its own string format, parser and try/catch (tabs for
/// the library filters, <c>;</c> and <c>=</c> for the model pins, <c>|</c> for Practice), and each
/// swallowed a different set of exceptions. Blocked site data, a private window or a value written
/// by an older build all read as "nothing remembered".
/// </summary>
public sealed class BrowserPrefs(IJSRuntime js)
{
    public const string LibraryFiltersKey = "pm-library-filters";
    public const string ExecutorPinsKey = "pm-executor-pins";
    public const string PracticeBackingKey = "pm-practice-backing";
    public const string InterpreterKey = "pm-interpreter";
    public const string SavedJamsKey = "pm-jam-saved";
    public const string JamSettingsKey = "pm-jam-settings";
    public const string JamPresetsKey = "pm-jam-presets";

    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> type)
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", key) is { } raw
                ? JsonSerializer.Deserialize(raw, type)
                : default;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>Fire-and-forget safe: a browser that refuses storage just forgets.</summary>
    public async Task SetAsync<T>(string key, T value, JsonTypeInfo<T> type)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value, type));
        }
        // QuotaExceededError and SecurityError are JSExceptions already; InvalidOperationException is
        // what a torn-down circuit throws. Anything else here would take the page down over a
        // preference, which is the opposite of what this type promises.
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", key);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }
}

/// <summary>The Library's search and filters as they were left.</summary>
public sealed record LibraryFilters(string Search);

/// <summary>Practice's backing as it was left, and whether the click was on. A value saved before the
/// click existed reads as on.</summary>
public sealed record PracticeBacking(int TonicPitchClass, ScaleMode Mode, string ProgressionId, double Bpm, bool Click = true);

/// <summary>A guitar jam song kept to play again: the song itself, chords and all, so it comes back as
/// it was even after the generator changes; its seed and sliders as well, which is all one saved before
/// the song was kept has to go on. The key and summary are its label in the list.</summary>
public sealed record SavedJam(int Seed, string Key, string Summary, ChordJamSettings? Settings = null, ChordJamDto? Song = null);

/// <summary>Guitar jam slider settings kept under a name, to start new songs from.</summary>
public sealed record JamPreset(string Name, ChordJamSettings Settings);

[JsonSerializable(typeof(LibraryFilters))]
[JsonSerializable(typeof(List<SavedJam>))]
[JsonSerializable(typeof(ChordJamSettings))]
[JsonSerializable(typeof(List<JamPreset>))]
[JsonSerializable(typeof(PracticeBacking))]
[JsonSerializable(typeof(AdvancedTools))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class PrefsJsonContext : JsonSerializerContext;
