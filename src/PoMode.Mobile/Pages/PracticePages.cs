using System.Globalization;
using PoMode.Mobile.Services;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Pages;

/// <summary>
/// Item 1: the live plot. The chord tones under the playhead as bands, your line drawn over them as you sing (or play),
/// lit where it lands on a chord tone. Like the web's, it reports no verdict: the answer is the analysis, after the take.
/// </summary>
public sealed class LivePlot : IDrawable
{
    private const int Low = 43;
    private const int High = 84;
    private const double Span = 8;
    private readonly Lock _gate = new();
    private readonly List<PitchPoint> _points = [];
    private IReadOnlyList<NoteEvent> _backing = [];
    private double _loop = 1;

    public double Now { get; set; }

    public void Prepare(IReadOnlyList<NoteEvent> backing, double loopSec)
    {
        lock (_gate)
        {
            _backing = backing;
            _loop = Math.Max(0.5, loopSec);
            _points.Clear();
            Now = 0;
        }
    }

    public void Add(PitchPoint p)
    {
        lock (_gate)
        {
            _points.Add(p);
            Now = Math.Max(Now, p.Sec);
        }
    }

    public List<PitchPoint> Recent(double seconds)
    {
        lock (_gate)
        {
            return [.. _points.Where(p => p.Sec >= Now - seconds)];
        }
    }

    private HashSet<int> ChordTonesAt(double t)
    {
        double at = ((t % _loop) + _loop) % _loop;
        return [.. _backing.Where(n => at >= n.StartSec && at < n.StartSec + n.DurationSec).Select(n => n.MidiPitch % 12)];
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        RectF rect = dirtyRect;
        lock (_gate)
        {
            double from = Math.Max(0, Now - (Span * 0.75));
            float X(double t) => rect.Left + (float)((t - from) / Span * rect.Width);
            float Y(double midi) => rect.Bottom - (float)((midi - Low) / (High - Low) * rect.Height);
            float row = rect.Height / (High - Low);

            canvas.FillColor = Colors.Gray.WithAlpha(0.08f);
            canvas.FillRectangle(rect);

            // Chord tones, every octave, in quarter-second strips.
            canvas.FillColor = Color.FromArgb("#4F46E5").WithAlpha(0.18f);
            for (double t = from; t < from + Span; t += 0.25)
            {
                foreach (int pc in ChordTonesAt(t))
                {
                    for (int midi = Low + (((pc - Low) % 12) + 12) % 12; midi <= High; midi += 12)
                    {
                        canvas.FillRectangle(X(t), Y(midi) - (row / 2), (float)(0.25 / Span * rect.Width) + 1, row);
                    }
                }
            }

            // C lines, so the octave can be read.
            canvas.StrokeColor = Colors.Gray.WithAlpha(0.3f);
            canvas.FontColor = Colors.Gray;
            canvas.FontSize = 10;
            for (int c = 48; c <= High; c += 12)
            {
                canvas.DrawLine(rect.Left, Y(c), rect.Right, Y(c));
                canvas.DrawString(Notes.Name(c), rect.Left + 2, Y(c) - 12, HorizontalAlignment.Left);
            }

            // Your line.
            canvas.StrokeSize = 3;
            PitchPoint? last = null;
            foreach (PitchPoint p in _points)
            {
                if (p.Sec < from || double.IsNaN(p.Midi))
                {
                    last = null;
                    continue;
                }

                if (last is { } l && p.Sec - l.Sec < 0.12)
                {
                    bool lit = ChordTonesAt(p.Sec).Contains(((int)Math.Round(p.Midi) % 12 + 12) % 12);
                    canvas.StrokeColor = lit ? Color.FromArgb("#15803D") : Color.FromArgb("#B45309");
                    canvas.DrawLine(X(l.Sec), Y(l.Midi), X(p.Sec), Y(p.Midi));
                }

                last = p;
            }

            canvas.StrokeColor = Colors.Gray;
            canvas.StrokeSize = 1;
            canvas.DrawLine(X(Now), rect.Top, X(Now), rect.Bottom);
        }
    }
}

/// <summary>
/// The web's Practice page: sing (or play a MIDI keyboard) over chords and find out what mode you sang; your earlier
/// takes and your range; the guitar jam; and the phone-only drone/metronome that keeps going with the screen locked.
/// </summary>
public sealed class PracticePage : ContentPage, IDisposable
{
    private const int CountInBeats = 4;
    private const double MaxTakeSeconds = 300;
    private static readonly string[] TabNames = ["Sing", "Takes", "Guitar jam", "Drone"];
    private readonly ApiClient _api;
    private readonly IMicrophone _mic;
    private readonly ITrackPlayerFactory _players;
    private readonly IMidiInput _midi;
    private readonly ToneMixer _mixer;
    private readonly IAudioOut _out;
    private readonly IToneSession _lockScreen;
    private readonly HorizontalStackLayout _tabs = new();
    private readonly ContentView _body = new();
    private readonly LivePlot _plot = new();
    private readonly GraphicsView _plotView;
    private readonly Label _vibrato = Ui.Muted("");
    private readonly Label _state = Ui.Muted("");
    private readonly PitchTracker _tracker = new();
    private readonly KeyboardTake _keys = new();
    private readonly MemoryStream _take = new();
    private string _tab = TabNames[0];
    private List<ChordProgressionDefinition> _progressions = [];
    private int _home = 2;
    private ScaleMode _mode = ScaleMode.Dorian;
    private string? _progressionId;
    private double _bpm = 92;
    private bool _click;
    private bool _useKeyboard;
    private GeneratedMelodyDto? _melody;
    private ModalMelodyRequest? _request;
    private VocalRangeDto? _voice;
    private ITrackPlayer? _player;
    private ITrackPlayer? _review;
    private IDispatcherTimer? _timer;
    private double _leadSec;
    private bool _recording;
    private byte[]? _captured;
    private ChordJamDto? _jam;
    private int _jamSeed = Random.Shared.Next(1, 1_000_000);
    private ChordJamSettings _jamSettings = new();

    public PracticePage(ApiClient api, IMicrophone mic, ITrackPlayerFactory players, IMidiInput midi, ToneMixer mixer, IAudioOut audioOut, IToneSession lockScreen)
    {
        _api = api;
        _mic = mic;
        _players = players;
        _midi = midi;
        _mixer = mixer;
        _out = audioOut;
        _lockScreen = lockScreen;
        Title = "Practice";
        _plotView = new GraphicsView { Drawable = _plot, HeightRequest = 260 };
        Content = new ScrollView
        {
            Content = Ui.Stack(10, new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _tabs }, _body),
            Padding = new Thickness(16),
        };
        RenderTabs();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_progressions.Count == 0)
        {
            try
            {
                _progressions = await _api.GetProgressionsAsync() ?? [];
                _progressionId = _progressions.SignatureFor(_mode)?.Id ?? _progressions.FirstOrDefault()?.Id;
            }
            catch (HttpRequestException ex)
            {
                _body.Content = Ui.Muted($"The server couldn't be reached: {ex.Message}");
                return;
            }
        }

        await ShowAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_recording)
        {
            _ = StopTakeAsync();
        }

        _player?.Pause();
        _review?.Pause();
    }

    private void RenderTabs()
    {
        _tabs.Clear();
        _tabs.Add(Ui.Tabs(TabNames, _tab, name =>
        {
            if (_recording)
            {
                return;
            }

            _tab = name;
            RenderTabs();
            _ = ShowAsync();
        }));
    }

    private async Task ShowAsync()
    {
        try
        {
            _body.Content = _tab switch
            {
                "Sing" => await SingAsync(),
                "Takes" => await TakesAsync(),
                "Guitar jam" => Jam(),
                _ => Drone(),
            };
        }
        catch (HttpRequestException ex)
        {
            _body.Content = Ui.Muted($"The server couldn't be reached: {ex.Message}");
        }
    }

    // ---- Sing ----------------------------------------------------------------------------------------------

    private ModalMelodyRequest CurrentRequest() => new(ScaleModes.ParentTonic(_home, _mode), _mode, _progressionId ?? "", _bpm, Seed: 42);

    private async Task<View> SingAsync()
    {
        Picker key = new() { Title = "Home note", ItemsSource = Enumerable.Range(0, 12).Select(i => ScaleModes.NoteName(i)).ToList(), SelectedIndex = _home };
        Picker mode = new() { Title = "Mode", ItemsSource = ScaleModes.All.Select(m => ScaleModes.DisplayName(m.ToString())).ToList(), SelectedIndex = ScaleModes.All.ToList().IndexOf(_mode) };
        Picker progression = new() { Title = "Chords", ItemsSource = _progressions.Select(p => $"{p.Name} ({p.RomanNumerals})").ToList(), SelectedIndex = _progressions.FindIndex(p => p.Id == _progressionId) };
        Slider tempo = new() { Minimum = 50, Maximum = 180, Value = _bpm, WidthRequest = 170 };
        Label tempoLabel = Ui.Muted(string.Create(CultureInfo.InvariantCulture, $"{_bpm:0} BPM"));
        tempo.ValueChanged += (_, e) => tempoLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(e.NewValue):0} BPM");
        tempo.DragCompleted += async (_, _) =>
        {
            _bpm = Math.Round(tempo.Value);
            await NewChordsAsync();
        };
        key.SelectedIndexChanged += async (_, _) =>
        {
            _home = key.SelectedIndex;
            await NewChordsAsync();
        };
        mode.SelectedIndexChanged += async (_, _) =>
        {
            _mode = ScaleModes.All[mode.SelectedIndex];
            _progressionId = _progressions.SignatureFor(_mode)?.Id ?? _progressionId;
            await NewChordsAsync(refreshPage: true);
        };
        progression.SelectedIndexChanged += async (_, _) =>
        {
            _progressionId = _progressions[progression.SelectedIndex].Id;
            await NewChordsAsync();
        };

        _voice = await _api.GetVocalRangeAsync(_mode);
        VerticalStackLayout voice = Ui.Stack(4);
        if (_voice is { } v)
        {
            voice.Add(Ui.Muted(v.Summary));
            if (v.Fit is { } fit)
            {
                int fittedHome = (fit.TonicPitchClass + ScaleModes.ModeDegreeOffset(_mode)) % 12;
                if (fittedHome != _home)
                {
                    voice.Add(Ui.Secondary("Fit to my voice", async () =>
                    {
                        _home = fittedHome;
                        await NewChordsAsync(refreshPage: true);
                        await Ui.Toast(fit.Explanation);
                    }));
                }
                else
                {
                    voice.Add(Ui.Muted($"Fitted to your voice: {fit.Explanation}"));
                }
            }
        }

        if (_melody is null)
        {
            await GenerateAsync();
        }

        Switch keyboard = Ui.Toggle(_useKeyboard, async on =>
        {
            if (on && _midi.Connected is null && !await ConnectKeyboardAsync())
            {
                _useKeyboard = false;
                await ShowAsync();
                return;
            }

            _useKeyboard = on;
        });
        keyboard.IsEnabled = _midi.IsSupported;

        return Ui.Stack(10,
            Ui.Text("Sing any melody over these chords and the analyser tells you which mode it turned out to be in. Nothing is scored."),
            Ui.Field("Home note", key),
            Ui.Field("Mode", mode),
            progression,
            Ui.Row(tempo, tempoLabel),
            voice,
            Ui.H2(_melody?.ProgressionName ?? ""),
            Ui.Text(string.Join("  ", _melody?.Chords.Select(c => c.Symbol) ?? [])),
            _plotView,
            Ui.Muted("chord tones · your line (green on a chord tone)"),
            _vibrato,
            Ui.Field("Play it on a MIDI keyboard instead", keyboard),
            Ui.Field("Click on every beat", Ui.Toggle(_click, on =>
            {
                _click = on;
                return Task.CompletedTask;
            })),
            _state,
            TakeButtons());
    }

    private View TakeButtons()
    {
        if (_recording)
        {
            return Ui.Danger("Stop", StopTakeAsync);
        }

        if (_captured is not null)
        {
            return Ui.Stack(8,
                Ui.Text(string.Create(CultureInfo.InvariantCulture, $"Take: {(_captured.Length - 44) / 2.0 / Wav.Rate:0.0}s")),
                Ui.Row(Ui.Secondary("Hear it back", ReviewAsync), Ui.Secondary("Redo", async () =>
                {
                    _captured = null;
                    await ShowAsync();
                })),
                Ui.Primary("What mode did I sing? →", AnalyseTakeAsync));
        }

        return Ui.Stack(8,
            Ui.Row(Ui.Secondary("Hear the chords", PreviewAsync), Ui.Secondary("New chords", async () =>
            {
                List<ChordProgressionDefinition> others = [.. _progressions.Where(p => p.Id != _progressionId && (p.SuggestedMode == _mode || p.RootsOn == HarmonicRoot.ParentTonic))];
                if (others.Count > 0)
                {
                    _progressionId = others[Random.Shared.Next(others.Count)].Id;
                }

                await NewChordsAsync(refreshPage: true);
            })),
            Ui.Primary(_useKeyboard ? "Play over them" : "Sing over them", StartTakeAsync));
    }

    private async Task NewChordsAsync(bool refreshPage = false)
    {
        StopPlayback();
        _captured = null;
        await GenerateAsync();
        if (refreshPage || _tab == "Sing")
        {
            await ShowAsync();
        }
    }

    private async Task GenerateAsync()
    {
        if (_progressionId is null)
        {
            return;
        }

        _request = CurrentRequest();
        _melody = await _api.GenerateAsync(_request);
        if (_melody is not null)
        {
            _plot.Prepare(_melody.BackingNotes, LoopSec);
            _plotView.Invalidate();
        }
    }

    private double LoopSec => _melody is { Chords.Count: > 0 } m ? m.Chords[^1].EndSec : 8;

    private string WriteBacking(int loops, int countIn)
    {
        byte[] wav = Render.Backing(_melody!.BackingNotes, LoopSec, _bpm, countIn, loops, _click);
        string path = Path.Combine(FileSystem.CacheDirectory, $"practice-{countIn}-{loops}.wav");
        File.WriteAllBytes(path, wav);
        return path;
    }

    private async Task PreviewAsync()
    {
        if (_melody is null)
        {
            return;
        }

        if (_player?.IsPlaying == true)
        {
            StopPlayback();
            return;
        }

        StopPlayback();
        _player = _players.Create();
        _player.Load(await Task.Run(() => WriteBacking(2, 0)));
        _player.Play();
    }

    private void StopPlayback()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _review?.Pause();
        _review?.Dispose();
        _review = null;
        _timer?.Stop();
    }

    private async Task StartTakeAsync()
    {
        if (_melody is null)
        {
            return;
        }

        if (!_useKeyboard && await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            await Ui.Toast("PoMode needs the microphone to hear you sing.");
            return;
        }

        StopPlayback();
        int loops = Math.Max(1, (int)Math.Ceiling(MaxTakeSeconds / LoopSec));
        loops = Math.Min(loops, 40);
        _state.Text = "Getting the chords ready…";
        string path = await Task.Run(() => WriteBacking(Math.Min(loops, Math.Max(1, (int)(180 / LoopSec))), CountInBeats));
        _leadSec = CountInBeats * 60.0 / _bpm;
        _take.SetLength(0);
        _tracker.Reset();
        _plot.Prepare(_melody.BackingNotes, LoopSec);
        _player = _players.Create();
        _player.Load(path);

        if (_useKeyboard)
        {
            _keys.Start();
            _out.Start(_mixer);
        }
        else if (!await _mic.StartAsync(OnMic, voiceProcessing: true))
        {
            await Ui.Toast("The microphone could not be opened.");
            return;
        }

        _player.Play();
        _recording = true;
        Haptics.Tap();
        _state.Text = "Counting you in…";
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) =>
        {
            double t = (_player?.PositionSec ?? 0) - _leadSec;
            _state.Text = t < 0 ? "Counting you in…" : string.Create(CultureInfo.InvariantCulture, $"● Recording · {t:0.0}s — sing anything you like over the chords.");
            if (_useKeyboard)
            {
                _plot.Now = Math.Max(_plot.Now, t);
            }

            _plotView.Invalidate();
            if (t > MaxTakeSeconds || _player?.IsPlaying == false)
            {
                _ = StopTakeAsync();
            }
        };
        _timer.Start();
        await ShowAsync();
    }

    /// <summary>Mic frames: kept for the take (after the count-in), and read for pitch on the spot.</summary>
    private void OnMic(byte[] frame)
    {
        lock (_take)
        {
            _take.Write(frame);
        }

        foreach (PitchPoint p in _tracker.Push(frame))
        {
            _plot.Add(p with { Sec = p.Sec - _leadSec });
        }

        string? vib = Vibrato.Describe(_plot.Recent(1.0));
        MainThread.BeginInvokeOnMainThread(() => _vibrato.Text = vib ?? "");
    }

    /// <summary>MIDI keyboard notes: heard through the phone, drawn on the plot, and kept for the take (item 5).</summary>
    private void OnKey(int note, int velocity)
    {
        if (velocity > 0)
        {
            _mixer.NoteOn(note, velocity);
        }
        else
        {
            _mixer.NoteOff(note);
        }

        if (!_recording || !_useKeyboard)
        {
            return;
        }

        _keys.Note(note, velocity);
        double t = _keys.Elapsed - _leadSec;
        _plot.Add(new PitchPoint(t, velocity > 0 ? note : double.NaN, 1));
    }

    private async Task StopTakeAsync()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        _timer?.Stop();
        _player?.Pause();
        byte[] pcm;
        if (_useKeyboard)
        {
            pcm = _keys.StopAndRender();
            if (!_mixer.Active)
            {
                _out.Stop();
            }
        }
        else
        {
            _mic.Stop();
            lock (_take)
            {
                pcm = _take.ToArray();
            }
        }

        // The take starts where the chords do: drop the count-in.
        int lead = Math.Min(pcm.Length, (int)(_leadSec * Wav.Rate) * 2);
        byte[] body = pcm[lead..];
        if (body.Length < Wav.Rate * 2 * 2)
        {
            _captured = null;
            _state.Text = "That take was under two seconds.";
        }
        else
        {
            _captured = Wav.Build(body);
            _state.Text = "";
        }

        Haptics.Tap();
        await ShowAsync();
    }

    private async Task ReviewAsync()
    {
        if (_captured is null || _melody is null)
        {
            return;
        }

        StopPlayback();
        string takePath = Path.Combine(FileSystem.CacheDirectory, "take.wav");
        await File.WriteAllBytesAsync(takePath, _captured);
        int loops = (int)Math.Ceiling(((_captured.Length - 44) / 2.0 / Wav.Rate) / LoopSec) + 1;
        _player = _players.Create();
        _player.Load(await Task.Run(() => WriteBacking(loops, 0)));
        _player.Volume = 0.6;
        _review = _players.Create();
        _review.Load(takePath);
        _player.Play();
        _review.Play();
    }

    private async Task AnalyseTakeAsync()
    {
        if (_captured is null || _request is null)
        {
            return;
        }

        StopPlayback();
        JobStatusDto? job = await _api.UploadHumTakeAsync(_captured, _request);
        if (job is null)
        {
            await Ui.Toast("The take couldn't be sent. Try again in a moment.");
            return;
        }

        _captured = null;
        await Shell.Current.GoToAsync($"song?id={job.JobId}");
    }

    private async Task<bool> ConnectKeyboardAsync()
    {
        if (!_midi.IsSupported)
        {
            await Ui.Toast("This phone has no MIDI support.");
            return false;
        }

        IReadOnlyList<string> devices = await _midi.ListAsync();
        string scan = "Look for Bluetooth keyboards…";
        string pick = await DisplayActionSheetAsync("Connect a MIDI keyboard (USB or Bluetooth)", "Cancel", null, [.. devices, scan]);
        if (pick == scan)
        {
            _state.Text = "Looking for Bluetooth MIDI keyboards…";
            await _midi.ScanBluetoothAsync();
            devices = await _midi.ListAsync();
            _state.Text = "";
            pick = await DisplayActionSheetAsync(devices.Count == 0 ? "No keyboards found" : "Connect", "Cancel", null, [.. devices]);
        }

        if (!devices.Contains(pick))
        {
            return false;
        }

        bool ok = await _midi.OpenAsync(pick, (note, velocity) => MainThread.BeginInvokeOnMainThread(() => OnKey(note, velocity)));
        if (ok)
        {
            _out.Start(_mixer);
        }

        await Ui.Toast(ok ? $"{pick} connected. Play to hear it." : $"{pick} wouldn't open.");
        return ok;
    }

    // ---- Takes ---------------------------------------------------------------------------------------------

    private async Task<View> TakesAsync()
    {
        VerticalStackLayout v = Ui.Stack(8);
        _voice = await _api.GetVocalRangeAsync(_mode);
        if (_voice?.Voice is { TypeLabel: not null } singer)
        {
            v.Add(Ui.H2("Your voice"));
            v.Add(Ui.Text($"{singer.TypeLabel}{(singer.Leaning is null ? "" : $" ({singer.Leaning})")}"));
            v.Add(Ui.Muted(singer.Summary));
            if (singer.StrongestSentence is { } strongest)
            {
                v.Add(Ui.Muted(strongest));
            }
        }

        if (_voice is { } range)
        {
            v.Add(Ui.Muted(range.Summary));
        }

        TakeHistoryDto? history = await _api.GetTakeHistoryAsync(CurrentRequest());
        v.Add(Ui.H2($"Takes over {_melody?.ProgressionName ?? "these chords"}"));
        v.Add(Ui.Muted(history?.Summary ?? ""));
        foreach (PastTakeDto t in history?.Takes ?? [])
        {
            v.Add(Ui.Card(Ui.Stack(2,
                Ui.Text(t.Reading),
                Ui.Muted($"{t.SungOver} · {t.CreatedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}")),
                () => Shell.Current.GoToAsync($"song?id={t.JobId}")));
        }

        return v;
    }

    // ---- Guitar jam ----------------------------------------------------------------------------------------

    private View Jam()
    {
        VerticalStackLayout v = Ui.Stack(8, Ui.Text("Open guitar chords that sound richer than they are to play. Strum along, or sing over it."));
        v.Add(JamSlider("Difficulty", _jamSettings.Difficulty, x => _jamSettings = _jamSettings with { Difficulty = x }));
        v.Add(JamSlider("Mood (sad → happy)", _jamSettings.Mood, x => _jamSettings = _jamSettings with { Mood = x }));
        v.Add(JamSlider("Tempo", _jamSettings.Tempo, x => _jamSettings = _jamSettings with { Tempo = x }));
        v.Add(JamSlider("Colour", _jamSettings.Colour, x => _jamSettings = _jamSettings with { Colour = x }));
        v.Add(JamSlider("Length", _jamSettings.Length, x => _jamSettings = _jamSettings with { Length = x }));
        v.Add(Ui.Primary("New jam", async () =>
        {
            _jamSeed = Random.Shared.Next(1, 1_000_000);
            _jam = await _api.GetChordJamAsync(_jamSeed, _jamSettings);
            await ShowAsync();
        }));
        if (_jam is not { } jam)
        {
            return v;
        }

        v.Add(Ui.H2(jam.Title ?? $"A jam in {jam.Key}"));
        v.Add(Ui.Muted(string.Create(CultureInfo.InvariantCulture,
            $"{jam.Feel} · {jam.Bpm:0} BPM · key {jam.Key}{(jam.Capo > 0 ? $" · capo {jam.Capo} (sounds in {jam.SoundingKey})" : "")}")));
        foreach (JamSectionDto section in jam.Form.Select(i => jam.Sections[i]).Distinct())
        {
            v.Add(Ui.Text($"{section.Name} ×{section.Repeats}  ({section.Numerals}) · strum {section.Pattern}"));
            v.Add(Ui.Colored(string.Join("   ", section.Chords.Select(c => $"{c.Symbol} {Frets(c.Frets)}")), Ui.Accent, 14));
        }

        v.Add(Ui.Muted($"Ending: {jam.Ending.Symbol} {Frets(jam.Ending.Frets)}"));
        if (jam.Lyrics is { Count: > 0 } lyrics)
        {
            v.Add(Ui.Muted(string.Join("\n", lyrics.Select(l => string.Join(" / ", l)))));
        }

        v.Add(Ui.Row(
            Ui.Secondary("Play it", () => PlayJamAsync(jam)),
            Ui.Secondary("Stop", () =>
            {
                StopPlayback();
                return Task.CompletedTask;
            })));
        v.Add(_recording
            ? Ui.Danger("Stop and send the take", () => StopJamTakeAsync(jam))
            : Ui.Primary("Sing over it", () => StartJamTakeAsync(jam)));
        v.Add(_state);
        v.Add(Ui.Secondary("Earlier takes over this jam", async () =>
        {
            TakeHistoryDto? h = await _api.GetJamTakesAsync(jam);
            string pick = await DisplayActionSheetAsync(h?.Summary ?? "No takes yet", "Close", null, [.. (h?.Takes ?? []).Select(t => t.Reading)]);
            if (h?.Takes.FirstOrDefault(t => t.Reading == pick) is { } chosen)
            {
                await Shell.Current.GoToAsync($"song?id={chosen.JobId}");
            }
        }));
        return v;
    }

    private static string Frets(int[] frets) => string.Concat(frets.Select(f => f < 0 ? "x" : f > 9 ? $"({f})" : f.ToString(CultureInfo.InvariantCulture)));

    private static Grid JamSlider(string label, double value, Action<double> set)
    {
        Slider s = new() { Minimum = 0, Maximum = 1, Value = value, WidthRequest = 170 };
        s.ValueChanged += (_, e) => set(e.NewValue);
        return Ui.Field(label, s);
    }

    private async Task PlayJamAsync(ChordJamDto jam, int countIn = 0)
    {
        StopPlayback();
        var (wav, lead) = await Task.Run(() => Render.Jam(jam, countIn));
        _leadSec = lead;
        string path = Path.Combine(FileSystem.CacheDirectory, "jam.wav");
        await File.WriteAllBytesAsync(path, wav);
        _player = _players.Create();
        _player.Load(path);
        _player.Play();
    }

    private async Task StartJamTakeAsync(ChordJamDto jam)
    {
        if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            return;
        }

        _take.SetLength(0);
        _tracker.Reset();
        await PlayJamAsync(jam, CountInBeats);
        if (!await _mic.StartAsync(frame =>
        {
            lock (_take)
            {
                _take.Write(frame);
            }
        }, voiceProcessing: true))
        {
            StopPlayback();
            await Ui.Toast("The microphone could not be opened.");
            return;
        }

        _recording = true;
        _state.Text = "● Recording — sing over the jam.";
        await ShowAsync();
    }

    private async Task StopJamTakeAsync(ChordJamDto jam)
    {
        _recording = false;
        _mic.Stop();
        StopPlayback();
        byte[] pcm;
        lock (_take)
        {
            pcm = _take.ToArray();
        }

        int lead = Math.Min(pcm.Length, (int)(_leadSec * Wav.Rate) * 2);
        _state.Text = "Sending the take…";
        var (job, error) = await _api.UploadJamTakeAsync(Wav.Build(pcm.AsSpan(lead)), jam, jam.Bpm);
        _state.Text = "";
        if (job is null)
        {
            await Ui.Toast(string.IsNullOrWhiteSpace(error) ? "The take couldn't be sent." : error);
            await ShowAsync();
            return;
        }

        await Shell.Current.GoToAsync($"song?id={job.JobId}");
    }

    // ---- Drone / metronome (item 6) ------------------------------------------------------------------------

    private View Drone()
    {
        Picker root = new() { Title = "Drone note", ItemsSource = Enumerable.Range(0, 12).Select(i => ScaleModes.NoteName(i)).ToList(), SelectedIndex = _mixer.DroneRoot };
        root.SelectedIndexChanged += (_, _) => _mixer.Set(root: root.SelectedIndex);
        Slider bpm = new() { Minimum = 30, Maximum = 240, Value = _mixer.Bpm, WidthRequest = 170 };
        Label bpmLabel = Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{_mixer.Bpm:0} BPM"));
        bpm.ValueChanged += (_, e) =>
        {
            _mixer.Set(bpm: Math.Round(e.NewValue));
            bpmLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{_mixer.Bpm:0} BPM");
        };
        Picker meter = new() { Title = "Beats per bar", ItemsSource = Enumerable.Range(1, 12).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList(), SelectedIndex = _mixer.BeatsPerBar - 1 };
        meter.SelectedIndexChanged += (_, _) => _mixer.Set(beatsPerBar: meter.SelectedIndex + 1);

        async Task ApplyAsync(bool? drone = null, bool? click = null)
        {
            _mixer.Set(drone: drone, click: click);
            if (_mixer.Active)
            {
                await RequestNotificationsAsync();
                _out.Start(_mixer);
                _lockScreen.Show(_mixer);
            }
            else
            {
                _lockScreen.Hide();
                if (_midi.Connected is null)
                {
                    _out.Stop();
                }
            }
        }

        return Ui.Stack(10,
            Ui.Text("A drone on the home note and a click to keep time. They keep playing with the screen locked, with play/pause and tempo on the lock screen."),
            Ui.Field("Drone note", root),
            Ui.Field("Drone", Ui.Toggle(_mixer.DroneOn, on => ApplyAsync(drone: on))),
            Ui.Field("Click", Ui.Toggle(_mixer.ClickOn, on => ApplyAsync(click: on))),
            Ui.Row(bpm, bpmLabel),
            Ui.Row(
                Ui.Secondary("−5", () => Nudge(-5)),
                Ui.Secondary("+5", () => Nudge(5)),
                Ui.Secondary("Tap tempo", TapTempo)),
            Ui.Field("Beats per bar", meter),
            Ui.Field("Volume", VolumeSlider()),
            Ui.Danger("Stop everything", () => ApplyAsync(drone: false, click: false)));

        Task Nudge(int by)
        {
            _mixer.Set(bpm: _mixer.Bpm + by);
            bpm.Value = _mixer.Bpm;
            return Task.CompletedTask;
        }
    }

    private readonly List<DateTime> _taps = [];

    private Task TapTempo()
    {
        DateTime now = DateTime.UtcNow;
        _taps.RemoveAll(t => (now - t).TotalSeconds > 3);
        _taps.Add(now);
        if (_taps.Count >= 3)
        {
            double gap = (_taps[^1] - _taps[0]).TotalSeconds / (_taps.Count - 1);
            _mixer.Set(bpm: Math.Round(60 / gap));
            _ = ShowAsync();
        }

        Haptics.Tap();
        return Task.CompletedTask;
    }

    private Slider VolumeSlider()
    {
        Slider s = new() { Minimum = 0, Maximum = 1, Value = _mixer.Volume, WidthRequest = 170 };
        s.ValueChanged += (_, e) => _mixer.Volume = e.NewValue;
        return s;
    }

    /// <summary>Android 13+ needs the notification permission for the lock-screen controls to show.</summary>
    private static async Task RequestNotificationsAsync()
    {
        if (DeviceInfo.Current.Platform == DevicePlatform.Android)
        {
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
    }

    public void Dispose()
    {
        _take.Dispose();
        _player?.Dispose();
        _review?.Dispose();
    }
}
