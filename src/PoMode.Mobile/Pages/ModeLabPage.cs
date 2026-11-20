using System.Globalization;
using PoMode.Mobile.Services;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Pages;

/// <summary>
/// The web's Mode Lab: pick a mode, a key and chords, and the server writes a melody in it — hear it, change it in
/// words ("darker and slower"), send it to the analyser, or share it as MIDI/WAV.
/// </summary>
public sealed class ModeLabPage : ContentPage
{
    private readonly MobileApi _api;
    private readonly ITrackPlayerFactory _players;
    private readonly ContentView _result = new();
    private readonly VerticalStackLayout _controls = Ui.Stack(8);
    private List<ChordProgressionDefinition> _progressions = [];
    private ModalMelodyRequest _request = new(0, ScaleMode.Dorian, "", 100);
    private int _home = 2;
    private GeneratedMelodyDto? _melody;
    private ITrackPlayer? _player;

    public ModeLabPage(MobileApi api, ITrackPlayerFactory players)
    {
        _api = api;
        _players = players;
        Title = "Mode Lab";
        Content = new ScrollView
        {
            Content = Ui.Stack(10, Ui.H1("Mode Lab"), Ui.Muted("Hear what a mode sounds like: a melody written in it, over chords that hold it in place."), _controls, _result),
            Padding = new Thickness(16),
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_progressions.Count > 0)
        {
            return;
        }

        try
        {
            _progressions = await _api.GetModalProgressionsAsync() ?? [];
        }
        catch (HttpRequestException ex)
        {
            _result.Content = Ui.Muted($"The server couldn't be reached: {ex.Message}");
            return;
        }

        _request = _request with { ProgressionId = _progressions.SignatureFor(_request.Mode)?.Id ?? _progressions.FirstOrDefault()?.Id ?? "" };
        Sync();
        RenderControls();
        await GenerateAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _player?.Pause();
    }

    private void Sync() => _request = _request with { TonicPitchClass = ScaleModes.ParentTonic(_home, _request.Mode) };

    private void RenderControls()
    {
        _controls.Clear();
        Picker key = new() { Title = "Home note", ItemsSource = Enumerable.Range(0, 12).Select(i => ScaleModes.NoteName(i)).ToList(), SelectedIndex = _home };
        Picker mode = new() { Title = "Mode", ItemsSource = ScaleModes.All.Select(m => ScaleModes.DisplayName(m.ToString())).ToList(), SelectedIndex = ScaleModes.All.ToList().IndexOf(_request.Mode) };
        Picker chords = new() { Title = "Chords", ItemsSource = _progressions.Select(p => $"{p.Name} · {p.Category}").ToList(), SelectedIndex = _progressions.FindIndex(p => p.Id == _request.ProgressionId) };
        Picker style = new() { Title = "Melody", ItemsSource = Enum.GetNames<MelodyStyle>(), SelectedIndex = (int)_request.Style };
        Slider tempo = new() { Minimum = 50, Maximum = 180, Value = _request.Bpm, WidthRequest = 170 };
        Slider purity = new() { Minimum = 50, Maximum = 100, Value = _request.TargetPurity, WidthRequest = 170 };
        key.SelectedIndexChanged += async (_, _) =>
        {
            _home = key.SelectedIndex;
            Sync();
            await GenerateAsync();
        };
        mode.SelectedIndexChanged += async (_, _) =>
        {
            ScaleMode m = ScaleModes.All[mode.SelectedIndex];
            _request = _request with { Mode = m, ProgressionId = _progressions.SignatureFor(m)?.Id ?? _request.ProgressionId };
            Sync();
            RenderControls();
            await GenerateAsync();
        };
        chords.SelectedIndexChanged += async (_, _) =>
        {
            _request = _request with { ProgressionId = _progressions[chords.SelectedIndex].Id };
            await GenerateAsync();
        };
        style.SelectedIndexChanged += async (_, _) =>
        {
            _request = _request with { Style = (MelodyStyle)style.SelectedIndex };
            await GenerateAsync();
        };
        tempo.DragCompleted += async (_, _) =>
        {
            _request = _request with { Bpm = Math.Round(tempo.Value) };
            await GenerateAsync();
        };
        purity.DragCompleted += async (_, _) =>
        {
            _request = _request with { TargetPurity = Math.Round(purity.Value) };
            await GenerateAsync();
        };

        Entry describe = new() { Placeholder = "Describe a change: \"darker and slower\"" };
        _controls.Add(Ui.Field("Home note", key));
        _controls.Add(Ui.Field("Mode", mode));
        _controls.Add(chords);
        _controls.Add(Ui.Field("Melody", style));
        _controls.Add(Ui.Field("Tempo", tempo));
        _controls.Add(Ui.Field("How strictly in the mode", purity));
        if (_progressions.Find(p => p.Id == _request.ProgressionId) is { } chosen)
        {
            _controls.Add(Ui.Muted($"{chosen.RomanNumerals} — {chosen.Description}"));
        }

        _controls.Add(describe);
        _controls.Add(Ui.Row(
            Ui.Secondary("Change it", async () =>
            {
                if (string.IsNullOrWhiteSpace(describe.Text))
                {
                    return;
                }

                ModeLabDescriptionDto? d = await _api.DescribeModeLabAsync(describe.Text.Trim(), _request);
                if (d is null)
                {
                    await Ui.Toast("That description couldn't be read.");
                    return;
                }

                await Ui.Toast(d.Sentence);
                if (d.Changed)
                {
                    _request = d.Request;
                    _home = (_request.TonicPitchClass + ScaleModes.ModeDegreeOffset(_request.Mode)) % 12;
                    RenderControls();
                    await GenerateAsync();
                }
            }),
            Ui.Secondary("Another melody", async () =>
            {
                _request = _request with { Seed = Random.Shared.Next(1, 100_000) };
                await GenerateAsync();
            })));
    }

    private async Task GenerateAsync()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _result.Content = new ActivityIndicator { IsRunning = true };
        try
        {
            _melody = await _api.GenerateModalMelodyAsync(_request);
        }
        catch (HttpRequestException ex)
        {
            _result.Content = Ui.Muted($"Couldn't write it: {ex.Message}");
            return;
        }

        if (_melody is not { } m)
        {
            _result.Content = Ui.Muted("The server couldn't write a melody for those settings.");
            return;
        }

        VerticalStackLayout v = Ui.Stack(8,
            Ui.H2($"{Notes.ModeName(m.TonicPitchClass, m.Mode)} · {m.ProgressionName}"),
            Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{m.ModePercentage:0}% of the melody is in the mode · {m.Bpm:0} BPM")),
            Ui.Muted($"Scale: {string.Join(' ', m.ScaleNotes)}"),
            Ui.Text(m.CharacteristicExplanation),
            Ui.Colored(string.Join("  ", m.Chords.Select(c => c.Symbol)), Ui.Accent, 15));
        if (m.BetterFit is { } fit)
        {
            v.Add(Ui.Colored(string.Create(CultureInfo.InvariantCulture, $"It drifted: it fits {fit.Name} better ({fit.Percentage:0}%)."), Ui.Warn));
        }

        v.Add(Ui.Row(
            Ui.Primary("Play", PlayAsync),
            Ui.Secondary("Stop", () =>
            {
                _player?.Pause();
                return Task.CompletedTask;
            })));
        v.Add(Ui.Row(
            Ui.Secondary("Analyse it", async () =>
            {
                if (await _api.AnalyzeModalMelodyAsync(_request) is { } job)
                {
                    await Shell.Current.GoToAsync($"song?id={job.JobId}");
                }
            }),
            Ui.Secondary("Share", ShareAsync)));
        _result.Content = v;
    }

    private async Task PlayAsync()
    {
        if (_player is null)
        {
            if (await _api.DownloadBackingWavAsync(_request) is not { } path)
            {
                await Ui.Toast("The audio couldn't be made.");
                return;
            }

            _player = _players.Create();
            _player.Load(path);
        }

        _player.Seek(0);
        _player.Play();
    }

    private async Task ShareAsync()
    {
        string pick = await DisplayActionSheetAsync("Share as", "Cancel", null, "MIDI", "WAV");
        string? path = pick switch
        {
            "MIDI" => await _api.DownloadAsync($"api/modal-melodies/midi?{MobileApi.BackingQuery(_request)}", $"modelab-{_request.Mode}.mid"),
            "WAV" => await _api.DownloadBackingWavAsync(_request),
            _ => null,
        };
        if (path is not null)
        {
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Mode Lab", File = new ShareFile(path) });
        }
    }
}
