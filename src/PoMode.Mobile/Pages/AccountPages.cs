using System.Globalization;
using PoMode.Mobile.Services;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;

namespace PoMode.Mobile.Pages;

/// <summary>The web's /settings: the account, the analysis models, the phone's own switches, and the way to /diag.</summary>
public sealed class SettingsPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly Session _session;
    private readonly MobileConfig _config;
    private readonly IMidiInput _midi;
    private readonly VerticalStackLayout _stack = Ui.Stack(10);

    public SettingsPage(ApiClient api, Session session, MobileConfig config, IMidiInput midi)
    {
        _api = api;
        _session = session;
        _config = config;
        _midi = midi;
        Title = "Settings";
        Content = new ScrollView { Content = _stack, Padding = new Thickness(16) };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        _stack.Clear();
        _stack.Add(Ui.H1("Settings"));

        _stack.Add(Ui.H2("Account"));
        try
        {
            await _session.EnsureAsync();
        }
        catch (HttpRequestException ex)
        {
            _stack.Add(Ui.Colored($"The server couldn't be reached: {ex.Message}", Ui.Bad));
        }

        _stack.Add(Ui.Text(_session.Label));
        _stack.Add(Ui.Muted("You're a guest on this phone: your songs are kept in this app's session."));
        _stack.Add(Ui.Secondary("Start over as a new guest", async () =>
        {
            if (await DisplayAlertAsync("Start over", "A new guest starts with an empty library. This guest's songs can't be got back.", "Start over", "Cancel"))
            {
                await _session.StartOverAsync();
                await RenderAsync();
            }
        }));

        _stack.Add(Ui.Danger("Delete all my songs", async () =>
        {
            if (await DisplayAlertAsync("Delete everything", "Delete every song and take you have on PoMode?", "Delete", "Keep"))
            {
                int gone = await _api.DeleteMyDataAsync();
                await Ui.Toast($"{gone} deleted.");
            }
        }));

        _stack.Add(Ui.H2("Analysis models"));
        _stack.Add(Ui.Muted("Which model runs each step of a new analysis. Auto lets the server choose."));
        try
        {
            foreach (StageExecutorsDto stage in await _api.GetExecutorsAsync() ?? [])
            {
                if (!StageNames.ExecutorQueryKeys.Any(k => k.Stage == stage.Stage))
                {
                    continue;
                }

                List<string> names = ["Auto", .. stage.Executors.Where(e => e.Available).Select(e => e.Name)];
                string key = $"pomode_exec_{stage.Stage}";
                string current = Preferences.Default.Get(key, "");
                Picker picker = new() { ItemsSource = names, SelectedIndex = Math.Max(0, names.IndexOf(current)), WidthRequest = 190 };
                picker.SelectedIndexChanged += (_, _) =>
                    Preferences.Default.Set(key, picker.SelectedIndex <= 0 ? "" : names[picker.SelectedIndex]);
                _stack.Add(Ui.Field(stage.Stage, picker));
            }
        }
        catch (HttpRequestException)
        {
        }

        _stack.Add(Ui.H2("This phone"));
        _stack.Add(Ui.Field("Haptics", Ui.Toggle(Haptics.Enabled, on =>
        {
            Haptics.Enabled = on;
            return Task.CompletedTask;
        })));
        _stack.Add(Ui.Field(_midi.Connected is { } k ? $"MIDI keyboard: {k}" : "MIDI keyboard", Ui.Secondary(_midi.Connected is null ? "Find" : "Disconnect", async () =>
        {
            if (_midi.Connected is not null)
            {
                _midi.Close();
            }
            else
            {
                await _midi.ScanBluetoothAsync();
                IReadOnlyList<string> devices = await _midi.ListAsync();
                await Ui.Toast(devices.Count == 0 ? "No MIDI keyboards found. Plug one in over USB, or switch a Bluetooth one on and try again." : $"Found: {string.Join(", ", devices)}. Connect it on the Practice tab.");
            }

            await RenderAsync();
        })));

#if DEBUG
        Entry url = new() { Text = _config.BaseUrl };
        _stack.Add(Ui.H2("Server (Debug)"));
        _stack.Add(url);
        _stack.Add(Ui.Secondary("Use this server", async () =>
        {
            _config.BaseUrl = url.Text;
            await Ui.Toast($"Using {_config.BaseUrl}. Restart the app to reconnect.");
        }));
#endif

        _stack.Add(Ui.H2("About"));
        _stack.Add(Ui.Secondary("Diagnostics", () => Shell.Current.GoToAsync("diag")));
        _stack.Add(Ui.Muted($"PoMode {AppInfo.Current.VersionString} · {_config.BaseUrl}"));
    }
}

/// <summary>The web's /diag: one card per dependency (API, Data, Services), read from the anonymous /api/diag.</summary>
public sealed class DiagPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly VerticalStackLayout _stack = Ui.Stack(8);

    public DiagPage(ApiClient api)
    {
        _api = api;
        Title = "Diagnostics";
        Content = new ScrollView { Content = _stack, Padding = new Thickness(16) };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _stack.Clear();
        DiagnosticsReport? report;
        try
        {
            report = await _api.GetDiagnosticsAsync();
        }
        catch (HttpRequestException ex)
        {
            _stack.Add(Ui.Card(Ui.Stack(2, Ui.Text("API"), Ui.Colored($"Unreachable: {ex.Message}", Ui.Bad)), stripe: Ui.Bad));
            return;
        }

        if (report is null)
        {
            return;
        }

        _stack.Add(Ui.Card(Ui.Stack(2, Ui.Text("API"), Ui.Colored($"{report.Status} · {report.EnvironmentName}", Colour(report.Status)),
            Ui.Muted($"Checked {report.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)} · {report.QueueDepth} in the queue")), stripe: Colour(report.Status)));
        Group("Data", report.Stores);
        Group("Services", report.Services);
        if (report.DefaultPlan is { Count: > 0 } plan)
        {
            _stack.Add(Ui.H2("Default plan"));
            foreach (StagePlan p in plan)
            {
                _stack.Add(Ui.Muted($"{p.Stage}: {p.Executor} ({p.Tier}){(p.IsPlaceholder ? " — USING MOCK DATA" : "")}"));
            }
        }
    }

    private void Group(string name, IReadOnlyList<DiagnosticCheck> checks)
    {
        _stack.Add(Ui.H2(name));
        foreach (DiagnosticCheck c in checks)
        {
            string latency = c.LatencyMs is { } ms ? string.Create(CultureInfo.InvariantCulture, $" · {ms:0} ms") : "";
            _stack.Add(Ui.Card(Ui.Stack(2, Ui.Text(c.Name), Ui.Colored($"{c.Status}{latency}", Colour(c.Status)), Ui.Muted(c.Description)), stripe: Colour(c.Status)));
        }
    }

    private static Color Colour(string status) => status switch
    {
        "Healthy" => Ui.Good,
        "Degraded" => Ui.Warn,
        _ => Ui.Bad,
    };
}
