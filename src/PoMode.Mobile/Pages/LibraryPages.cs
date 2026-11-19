using System.Globalization;
using PoMode.Mobile.Services;
using PoMode.Shared.Analysis;

namespace PoMode.Mobile.Pages;

/// <summary>The web's Home: bring a song in (pick a file, record one), what is on its way up, and the library.</summary>
public sealed class LibraryPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly Uploads _uploads;
    private readonly Session _session;
    private readonly VerticalStackLayout _pending = new() { Spacing = 6 };
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly Label _who = Ui.Muted("");
    private readonly Label _note = Ui.Muted("");
    private readonly Entry _search = new() { Placeholder = "Search your songs (name, key, mode)" };
    private List<LibraryEntryDto> _entries = [];
    private Dictionary<string, double>? _soundRank;

    public LibraryPage(ApiClient api, Uploads uploads, Session session)
    {
        _api = api;
        _uploads = uploads;
        _session = session;
        Title = "Songs";
        _search.TextChanged += (_, _) =>
        {
            _soundRank = null;
            Render();
        };
        RefreshView refresh = new()
        {
            Content = new ScrollView
            {
                Content = Ui.Stack(10,
                    Ui.H1("PoMode"),
                    _who,
                    Ui.Text("Find the mode of a song: upload it, and PoMode reads the melody against the chords."),
                    Ui.Row(
                        Ui.Primary("Pick a song", PickAsync),
                        Ui.Secondary("Record a song", () => Shell.Current.GoToAsync("record"))),
                    Ui.Muted(".mp3, .wav or .m4a · up to 100 MB. It keeps uploading with the screen locked, and you get a notification when it's analysed."),
                    _pending,
                    Ui.H2("Your songs"),
                    _search,
                    Ui.Row(Ui.Secondary("Search by sound", SoundSearchAsync)),
                    _note,
                    _list),
                Padding = new Thickness(16),
            },
        };
        refresh.Command = new Command(async () =>
        {
            await LoadAsync();
            refresh.IsRefreshing = false;
        });
        Content = refresh;
        _uploads.Changed += () => _ = RenderPendingAsync();
        _session.Changed += () => MainThread.BeginInvokeOnMainThread(() => _who.Text = $"Signed in as {_session.Label}");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await RenderPendingAsync();
        try
        {
            if (_session.Current is null)
            {
                await _session.EnsureAsync();
            }

            _entries = await _api.GetLibraryAsync();
            await LibraryCache.SaveAsync(_entries);
            _note.Text = "";
        }
        catch (HttpRequestException)
        {
            _entries = await LibraryCache.LoadAsync();
            _note.Text = "Offline — showing your songs as they were last time.";
        }

        Render();
    }

    private async Task PickAsync()
    {
        FileResult? picked = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Choose a song",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.Android] = ["audio/*"],
                [DevicePlatform.iOS] = ["public.audio"],
            }),
        });
        if (picked is null)
        {
            return;
        }

        string ext = Path.GetExtension(picked.FileName).ToUpperInvariant();
        if (ext is not (".MP3" or ".WAV" or ".M4A"))
        {
            await Ui.Toast("PoMode reads .mp3, .wav and .m4a files.");
            return;
        }

        Haptics.Tap();
        await _uploads.EnqueueAsync(picked);
    }

    private async Task SoundSearchAsync()
    {
        string words = _search.Text?.Trim() ?? "";
        if (words.Length < 2)
        {
            await Ui.Toast("Type what it sounds like first, e.g. \"slow sad piano\".");
            return;
        }

        List<SoundMatchDto>? matches = await _api.SearchBySoundAsync(words);
        if (matches is null)
        {
            await Ui.Toast("This server can't search by sound.");
            return;
        }

        _soundRank = matches.ToDictionary(m => m.JobId, m => m.Score);
        Render();
    }

    private async Task RenderPendingAsync()
    {
        IReadOnlyList<PendingUpload> items = await _uploads.ListAsync();
        _pending.Clear();
        if (items.Count == 0)
        {
            return;
        }

        _pending.Add(Ui.H2("On its way"));
        foreach (PendingUpload u in items.OrderByDescending(u => u.CreatedUtc))
        {
            Color stripe = u.State switch
            {
                UploadState.Done => Ui.Good,
                UploadState.Failed => Ui.Bad,
                _ => Ui.Accent,
            };
            HorizontalStackLayout actions = Ui.Row();
            if (u.State is UploadState.Failed)
            {
                actions.Add(Ui.Secondary("Retry", async () =>
                {
                    await _uploads.RetryAsync(u.Id);
                    _uploads.Kick();
                }));
            }

            if (u.State is UploadState.Done or UploadState.Failed or UploadState.Waiting)
            {
                actions.Add(Ui.Secondary(u.State is UploadState.Done ? "Hide" : "Remove", () => _uploads.RemoveAsync(u.Id)));
            }

            ProgressBar bar = new() { Progress = u.Size == 0 ? 0 : u.Offset / (double)u.Size, IsVisible = u.State is UploadState.Uploading };
            _pending.Add(Ui.Card(
                Ui.Stack(4, Ui.Text(u.FileName), Ui.Muted(u.Error ?? u.Status ?? u.State.ToString()), bar, actions),
                u.JobId is { } job ? () => Shell.Current.GoToAsync($"song?id={job}") : null,
                stripe));
        }
    }

    private void Render()
    {
        _list.Clear();
        string q = _search.Text?.Trim() ?? "";
        IEnumerable<LibraryEntryDto> rows = _entries;
        if (_soundRank is not null)
        {
            rows = rows.Where(e => _soundRank.ContainsKey(e.JobId)).OrderByDescending(e => _soundRank[e.JobId]);
        }
        else if (q.Length > 0)
        {
            rows = rows.Where(e => $"{e.Identity?.Title} {e.Identity?.Artist} {e.FileName} {e.TonicName} {e.PrimaryMode}".Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        List<LibraryEntryDto> shown = [.. rows];
        if (shown.Count == 0)
        {
            _list.Add(Ui.Muted(_entries.Count == 0 ? "No songs yet. Pick one above." : "Nothing matches."));
            return;
        }

        foreach (LibraryEntryDto e in shown)
        {
            string title = e.Identity is { } id ? $"{id.Title}{(id.Artist is null ? "" : $" — {id.Artist}")}" : e.FileName;
            string headline = e.Stage switch
            {
                JobStage.Complete when e.Origin?.Kind == TakeOrigin.Beat => "Drum loop (Mode Lab)",
                JobStage.Complete => string.Join(" · ", new[]
                {
                    e.TonicName is null ? null : $"{e.TonicName} {ScaleModes.DisplayName(e.PrimaryMode) ?? ""}".Trim(),
                    e.TempoBpm is { } bpm ? string.Create(CultureInfo.InvariantCulture, $"{bpm:0} BPM") : null,
                }.Where(s => s is not null)),
                JobStage.Failed => "Couldn't be analysed",
                JobStage.Cancelled => "Cancelled",
                _ => "Analysing…",
            };
            VerticalStackLayout body = Ui.Stack(2, Ui.Text(title), Ui.Colored(headline, Ui.Accent, 14));
            if (e.Origin is { Description: { Length: > 0 } why })
            {
                body.Add(Ui.Muted(why, 12));
            }

            body.Add(Ui.Muted(e.CreatedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture), 12));
            _list.Add(Ui.Card(body, () => e.Origin?.Kind == TakeOrigin.Beat
                ? Ui.Toast("A drum loop is kept for the Mode Lab and has no analysis to show.")
                : Shell.Current.GoToAsync($"song?id={e.JobId}")));
        }
    }
}

/// <summary>Record a song with the phone's microphone and send it like a picked file (the web's "Record a song").</summary>
public sealed class RecordSongPage : ContentPage, IDisposable
{
    private readonly IMicrophone _mic;
    private readonly Uploads _uploads;
    private readonly MemoryStream _pcm = new();
    private readonly Label _time = Ui.H1("0:00");
    private readonly ProgressBar _level = new();
    private readonly Button _start;
    private readonly Button _stop;
    private bool _recording;

    public RecordSongPage(IMicrophone mic, Uploads uploads)
    {
        _mic = mic;
        _uploads = uploads;
        Title = "Record a song";
        _start = Ui.Primary("Start recording", StartAsync);
        _stop = Ui.Danger("Stop and analyse", StopAsync);
        _stop.IsVisible = false;
        Content = new ScrollView
        {
            Content = Ui.Stack(12,
                Ui.Text("Hold the phone near the music. Thirty seconds is enough to find the mode; a whole song gives the sections too."),
                _time,
                _level,
                _start,
                _stop),
            Padding = new Thickness(16),
        };
    }

    private async Task StartAsync()
    {
        if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            await Ui.Toast("PoMode needs the microphone to record.");
            return;
        }

        _pcm.SetLength(0);
        DateTime started = DateTime.UtcNow;
        bool ok = await _mic.StartAsync(frame =>
        {
            lock (_pcm)
            {
                _pcm.Write(frame);
            }

            double peak = 0;
            for (int i = 0; i + 1 < frame.Length; i += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(frame, i) / 32768.0));
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _level.Progress = peak;
                _time.Text = (DateTime.UtcNow - started).ToString(@"m\:ss", CultureInfo.InvariantCulture);
            });
        }, voiceProcessing: false);
        if (!ok)
        {
            await Ui.Toast("The microphone wouldn't start.");
            return;
        }

        Haptics.Tap();
        _recording = true;
        _start.IsVisible = false;
        _stop.IsVisible = true;
    }

    private async Task StopAsync()
    {
        _mic.Stop();
        _recording = false;
        byte[] wav;
        lock (_pcm)
        {
            wav = Wav.Build(_pcm.ToArray());
        }

        if (wav.Length < 44 + (Wav.Rate * 2 * 5))
        {
            await Ui.Toast("That's under five seconds — too short to find a mode.");
            _start.IsVisible = true;
            _stop.IsVisible = false;
            return;
        }

        string path = Path.Combine(FileSystem.CacheDirectory, $"Recording {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}.wav");
        await File.WriteAllBytesAsync(path, wav);
        await _uploads.EnqueueAsync(new FileResult(path));
        await Shell.Current.GoToAsync("..");
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_recording)
        {
            _mic.Stop();
            _recording = false;
        }
    }

    public void Dispose() => _pcm.Dispose();
}

/// <summary>One song: live progress while it is analysed, then everything the web's song page shows, in tabs.</summary>
[QueryProperty(nameof(JobId), "id")]
public sealed class SongPage : ContentPage, IDisposable
{
    private static readonly string[] TabNames = ["Overview", "Chords", "Listen", "Stats", "Ask", "More"];
    private readonly ApiClient _api;
    private readonly JobWatcher _watcher;
    private readonly ITrackPlayerFactory _players;
    private readonly ContentView _body = new();
    private readonly HorizontalStackLayout _tabsHost = new();
    private readonly Label _title = Ui.H1("");
    private readonly Label _headline = Ui.Colored("", Ui.Accent, 18);
    private readonly List<InterpretationTurn> _history = [];
    private string _tab = TabNames[0];
    private JobStatusDto? _status;
    private ModalResult? _result;
    private VisualizationPayload? _visual;
    private SongStats? _stats;
    private string[] _stems = [];
    private ITrackPlayer? _vocals;
    private ITrackPlayer? _backing;
    private IDispatcherTimer? _clock;
    private List<LyricLineDto>? _lyrics;
    private CancellationTokenSource? _asking;

    public SongPage(ApiClient api, JobWatcher watcher, ITrackPlayerFactory players)
    {
        _api = api;
        _watcher = watcher;
        _players = players;
        Title = "Song";
        _watcher.Updated += s => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (s.JobId != _status?.JobId)
            {
                return;
            }

            bool finished = s.Stage.IsTerminal() && _status?.Stage.IsTerminal() != true;
            _status = s;
            if (finished)
            {
                Haptics.Strong();
                await LoadAsync();
            }
            else
            {
                RenderProgress(null);
            }
        });
        _watcher.QueuePosition += (id, ahead) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (id == _status?.JobId && !_status.Stage.IsTerminal())
            {
                RenderProgress(ahead);
            }
        });
        Content = new ScrollView
        {
            Content = Ui.Stack(10, _title, _headline, new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _tabsHost }, _body),
            Padding = new Thickness(16),
        };
    }

    public string JobId { get; set; } = "";

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_status?.JobId != JobId)
        {
            await LoadAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vocals?.Pause();
        _backing?.Pause();
        _asking?.Cancel();
    }

    private async Task LoadAsync()
    {
        try
        {
            _status = await _api.GetStatusAsync(JobId);
        }
        catch (HttpRequestException ex)
        {
            _body.Content = Ui.Muted($"Couldn't load this song: {ex.Message}");
            return;
        }

        if (_status is null)
        {
            _body.Content = Ui.Muted("This song is gone.");
            return;
        }

        _title.Text = _status.Identity is { } id ? $"{id.Title}{(id.Artist is null ? "" : $" — {id.Artist}")}" : _status.FileName ?? "Song";
        if (!_status.Stage.IsTerminal())
        {
            _tabsHost.Clear();
            _headline.Text = "";
            RenderProgress(null);
            try
            {
                await _watcher.WatchAsync(JobId);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                // No live channel: progress still arrives on pull-to-refresh / reopening.
            }

            return;
        }

        if (_status.Stage != JobStage.Complete)
        {
            _tabsHost.Clear();
            _headline.Text = _status.Stage == JobStage.Cancelled ? "Cancelled" : "This song couldn't be analysed";
            _body.Content = Ui.Stack(8, Ui.Muted(_status.Error ?? ""), Ui.Danger("Delete it", DeleteAsync));
            return;
        }

        _result = await _api.GetResultAsync(JobId);
        _visual = await _api.GetVisualAsync(JobId);
        _stems = await _api.GetStemsAsync(JobId);
        _headline.Text = Headline();
        RenderTabs();
        await ShowTabAsync(_tab);
    }

    private string Headline() => _result is null
        ? ""
        : string.Create(CultureInfo.InvariantCulture,
            $"{_result.TonicName} {(_result.PrimaryMode is { } m ? ScaleModes.DisplayName(m.ToString()) : "— no clear mode")} · {_result.TempoBpm:0} BPM{(_result.TempoEstimated ? " (estimated)" : "")}");

    private void RenderTabs()
    {
        _tabsHost.Clear();
        _tabsHost.Add(Ui.Tabs(TabNames, _tab, name =>
        {
            _tab = name;
            RenderTabs();
            _ = ShowTabAsync(name);
        }));
    }

    private async void RenderProgress(int? ahead)
    {
        if (_status is null)
        {
            return;
        }

        double? eta = null;
        try
        {
            eta = await _api.GetEtaAsync(JobId);
        }
        catch (HttpRequestException)
        {
        }

        VerticalStackLayout stages = Ui.Stack(4);
        foreach (StagePlan plan in _status.Plan)
        {
            bool done = _status.CompletedStages.Contains(plan.Stage);
            bool now = StageNames.ForStage(_status.Stage) == plan.Stage;
            stages.Add(Ui.Colored($"{(done ? "✓" : now ? "▶" : "·")} {plan.Stage} — {plan.Executor}{(plan.IsPlaceholder ? " (mock)" : "")}", done ? Ui.Good : now ? Ui.Accent : Colors.Gray));
        }

        _body.Content = Ui.Stack(10,
            Ui.Text("Analysing…"),
            new ProgressBar { Progress = _status.Progress },
            Ui.Muted(ahead is > 0 ? $"{ahead} song(s) ahead of yours." : eta is { } s ? $"About {TimeSpan.FromSeconds(s):m\\:ss} left." : "Working."),
            stages,
            Ui.Muted("You can leave this screen: the song keeps going, and the Songs tab shows it when it's done."),
            Ui.Danger("Cancel", async () =>
            {
                await _api.CancelAsync(JobId);
                await LoadAsync();
            }));
    }

    private async Task ShowTabAsync(string tab)
    {
        _body.Content = new ActivityIndicator { IsRunning = true };
        try
        {
            _body.Content = tab switch
            {
                "Overview" => await OverviewAsync(),
                "Chords" => await ChordsAsync(),
                "Listen" => await ListenAsync(),
                "Stats" => await StatsAsync(),
                "Ask" => await AskAsync(),
                _ => More(),
            };
        }
        catch (HttpRequestException ex)
        {
            _body.Content = Ui.Muted($"Couldn't load this part: {ex.Message}");
        }
    }

    private async Task<View> OverviewAsync()
    {
        VerticalStackLayout v = Ui.Stack(8);
        if (_status?.Origin is { Description: { Length: > 0 } origin })
        {
            v.Add(Ui.Muted(origin));
        }

        if (_status?.Identity is { } id)
        {
            v.Add(Ui.Muted($"Identified by {id.Source}{(id.Album is null ? "" : $" · {id.Album}")}"));
        }

        if (_result is { TuningOffsetCents: not 0 } r)
        {
            v.Add(Ui.Muted(string.Create(CultureInfo.InvariantCulture, $"The recording sat {Math.Abs(r.TuningOffsetCents):0} cents {(r.TuningOffsetCents < 0 ? "flat" : "sharp")} of A=440; its notes were read after correcting that.")));
        }

        if (_result?.PrimaryMode is { } mode)
        {
            v.Add(Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{_result.PrimaryConfidence:P0} confident · scale {string.Join(' ', ScaleModes.NoteNames(_result.TonicPitchClass, mode))}")));
        }

        if (_visual?.Evidence is { } evidence)
        {
            v.Add(Ui.H2($"Why {evidence.Mode}?"));
            foreach (string reason in evidence.Reasons)
            {
                v.Add(Ui.Text($"• {reason}"));
            }

            if (evidence.CounterEvidence is { } against)
            {
                v.Add(Ui.Muted($"Against it: {against}"));
            }

            foreach (ModeContrast c in evidence.Contrasts)
            {
                v.Add(Ui.Muted(string.Create(CultureInfo.InvariantCulture,
                    $"{c.Degree} ({c.ToneName}) {c.MelodyPercent:0}% of the melody, {c.ChordCount} chords — vs {c.Rival}'s {c.RivalDegree} ({c.RivalToneName}) {c.RivalMelodyPercent:0}%, {c.RivalChordCount}{(c.RulesOut ? " · rules it out" : "")}")));
            }
        }

        if (_visual is { Sections.Count: > 0 } visual)
        {
            v.Add(Ui.H2("Sections"));
            foreach (VisualSection s in visual.Sections)
            {
                v.Add(Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{s.Letter} · {s.Label} · {TimeSpan.FromSeconds(s.StartSec):m\\:ss}–{TimeSpan.FromSeconds(s.EndSec):m\\:ss}{(s.Mode is null ? "" : $" · {s.Mode}")}")));
            }
        }

        if (await _api.GetVoiceProfileAsync(JobId) is { } voice)
        {
            v.Add(Ui.H2("The singer"));
            v.Add(Ui.Text(voice.Summary));
            if (voice.StrongestSentence is { } strongest)
            {
                v.Add(Ui.Muted(strongest));
            }
        }

        if (await _api.GetSongVoiceFitAsync(JobId) is { } fit)
        {
            v.Add(Ui.H2("For your voice"));
            v.Add(Ui.Text(fit.Sentence));
        }

        if (await _api.GetSimilarAsync(JobId) is { Count: > 0 } similar)
        {
            v.Add(Ui.H2("Like this one"));
            foreach (SimilarSongDto s in similar.Take(5))
            {
                v.Add(Ui.Card(Ui.Stack(2, Ui.Text(s.FileName), Ui.Muted(s.Reason)), () => Shell.Current.GoToAsync($"song?id={s.JobId}")));
            }
        }

        return v;
    }

    private async Task<View> ChordsAsync()
    {
        VerticalStackLayout v = Ui.Stack(8);
        if (await _api.GetChordShapesAsync(JobId) is { } shapes)
        {
            v.Add(Ui.H2(shapes.CapoFret > 0 ? $"Guitar: capo {shapes.CapoFret}" : "Guitar"));
            v.Add(Ui.Muted(shapes.Advice));
            v.Add(Ui.Text(string.Join("   ", shapes.Chords.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.ShapeSymbol}{(c.ShapeSymbol != c.Symbol ? $" ({c.Symbol})" : "")} {c.Percent:0}%")))));
        }

        if (await _api.GetSongLoopAsync(JobId) is { } loop)
        {
            v.Add(Ui.H2("The loop"));
            v.Add(Ui.Text($"{string.Join(" → ", loop.Symbols)}  ({loop.Numerals}) × {loop.Repeats}"));
            if (loop.CanFindSongs)
            {
                v.Add(Ui.Secondary("Songs on these chords", async () =>
                {
                    List<ProgressionSongDto>? songs = await _api.GetSongsOnLoopAsync(JobId);
                    if (songs is not { Count: > 0 })
                    {
                        await Ui.Toast("No published songs found on these chords.");
                        return;
                    }

                    string pick = await DisplayActionSheetAsync("Songs on these chords", "Close", null, [.. songs.Take(15).Select(s => $"{s.Artist} — {s.Song} ({s.Section})")]);
                    if (songs.Find(s => $"{s.Artist} — {s.Song} ({s.Section})" == pick) is { } chosen)
                    {
                        await Launcher.Default.OpenAsync(chosen.Url);
                    }
                }));
            }
        }

        if (_visual is { } visual)
        {
            v.Add(Ui.H2("Bar by bar"));
            foreach (VisualWindow w in visual.Windows)
            {
                string mode = w.InsufficientEvidence ? "not enough melody" : w.ModeTag is { } tag ? string.Create(CultureInfo.InvariantCulture, $"{tag} {w.ModeConfidence:P0}") : "—";
                Grid row = new() { ColumnDefinitions = [new(new GridLength(46)), new(new GridLength(80)), new(GridLength.Star)], ColumnSpacing = 8 };
                row.Add(Ui.Muted($"bar {w.MeasureNumber}"));
                row.Add(Ui.Text(w.ChordSymbol), 1);
                row.Add(Ui.Muted(mode), 2);
                v.Add(row);
                if (w.Evidence is { } why)
                {
                    v.Add(Ui.Muted($"   {why}", 12));
                }
            }
        }

        return v;
    }

    private async Task<View> ListenAsync()
    {
        VerticalStackLayout v = Ui.Stack(10);
        Label position = Ui.Muted("");
        Label lyric = Ui.Text("");
        _lyrics ??= await _api.GetLyricsAsync(JobId);
        bool split = _stems.Contains("vocals") && _stems.Contains("instrumental");
        if (_backing is null)
        {
            v.Add(Ui.Muted("Downloading the audio…"));
            _body.Content = v;
            string mixExt = Path.GetExtension(_status?.FileName) is { Length: > 0 } ext ? ext : ".wav";
            string? main = await _api.DownloadAsync($"api/analysis/{JobId}/stems/{(split ? "instrumental" : "mix")}", $"{JobId}-{(split ? "instrumental.wav" : $"mix{mixExt}")}");
            if (main is null)
            {
                return Ui.Muted("The audio for this song isn't kept any more.");
            }

            _backing = _players.Create();
            _backing.Load(main);
            if (split && await _api.DownloadAsync($"api/analysis/{JobId}/stems/vocals", $"{JobId}-vocals.wav") is { } vocalsPath)
            {
                _vocals = _players.Create();
                _vocals.Load(vocalsPath);
            }

            v.Clear();
        }

        Slider seek = new() { Minimum = 0, Maximum = Math.Max(1, _backing.DurationSec) };
        seek.DragCompleted += (_, _) =>
        {
            _backing.Seek(seek.Value);
            _vocals?.Seek(seek.Value);
        };
        v.Add(Ui.Row(
            Ui.Primary("Play / pause", () =>
            {
                if (_backing.IsPlaying)
                {
                    _backing.Pause();
                    _vocals?.Pause();
                }
                else
                {
                    _vocals?.Seek(_backing.PositionSec);
                    _backing.Play();
                    _vocals?.Play();
                }

                return Task.CompletedTask;
            })));
        v.Add(seek);
        v.Add(position);
        if (_vocals is not null)
        {
            v.Add(Ui.H2("Mix"));
            v.Add(Ui.Field("Voice", Fader(_vocals)));
            v.Add(Ui.Field("Band", Fader(_backing)));
            v.Add(Ui.Muted("Turn the voice down to sing along, or the band down to hear the melody PoMode read."));
        }
        else
        {
            v.Add(Ui.Muted("This song wasn't split into voice and band, so it plays as recorded."));
        }

        if (_lyrics is { Count: > 0 })
        {
            v.Add(Ui.H2("Lyrics"));
            v.Add(lyric);
        }

        _clock?.Stop();
        _clock = Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(250);
        _clock.Tick += (_, _) =>
        {
            double at = _backing.PositionSec;
            if (!seek.IsFocused)
            {
                seek.Value = at;
            }

            string chord = _visual?.Windows.FirstOrDefault(w => at >= w.StartSec && at < w.EndSec) is { } w ? $" · {w.ChordSymbol}{(w.ModeTag is null ? "" : $" ({w.ModeTag})")}" : "";
            position.Text = $"{TimeSpan.FromSeconds(at):m\\:ss} / {TimeSpan.FromSeconds(_backing.DurationSec):m\\:ss}{chord}";
            lyric.Text = _lyrics?.LastOrDefault(l => l.StartSec <= at)?.Text ?? "";
        };
        _clock.Start();
        return v;
    }

    private static Slider Fader(ITrackPlayer player)
    {
        Slider s = new() { Minimum = 0, Maximum = 1, Value = player.Volume, WidthRequest = 180 };
        s.ValueChanged += (_, e) => player.Volume = e.NewValue;
        return s;
    }

    private async Task<View> StatsAsync()
    {
        _stats ??= await _api.GetStatsAsync(JobId);
        if (_stats is not { } s)
        {
            return Ui.Muted("No statistics for this song yet.");
        }

        VerticalStackLayout v = Ui.Stack(6, Ui.Text(s.Fingerprint));
        v.Add(Ui.H2("Melody"));
        v.Add(Ui.Text(string.Create(CultureInfo.InvariantCulture,
            $"{s.MelodyNoteCount} notes · {s.NotesPerSecond:0.0} a second · {s.InScalePercent:0}% in the scale · {s.ModulationCount} key change(s) · {TimeSpan.FromSeconds(s.DurationSec):m\\:ss} long")));
        if (s.BiggestLeap is { } leap)
        {
            v.Add(Ui.Muted($"Biggest leap: {leap}"));
        }

        v.Add(Ui.H2("Mode votes"));
        foreach (ModeVote vote in s.ModeVotes)
        {
            v.Add(Ui.Text(string.Create(CultureInfo.InvariantCulture,
                $"{(vote.IsPrimary ? "★ " : "")}{ScaleModes.DisplayName(vote.Mode)} — {vote.WindowPercent:0}% of bars ({vote.WindowsWon}), {vote.AverageConfidence:P0} sure{(vote.CharacteristicDegrees.Count > 0 ? $" · {string.Join(", ", vote.CharacteristicDegrees)}" : "")}")));
        }

        v.Add(Ui.H2("Scale degrees"));
        foreach (DegreeUsage d in s.ScaleDegrees.OrderByDescending(d => d.Percent))
        {
            v.Add(Ui.Text(string.Create(CultureInfo.InvariantCulture,
                $"{d.DegreeLabel} ({d.NoteName}) {d.Percent:0}%{(d.IsCharacteristic ? " · characteristic" : "")}{(d.InPrimaryMode ? "" : " · outside the mode")}")));
        }

        return v;
    }

    private async Task<View> AskAsync()
    {
        List<InterpreterOptionDto> interpreters = (await _api.GetInterpretersAsync())?.Where(i => i.Available).ToList() ?? [];
        Picker writer = new() { Title = "Written by", ItemsSource = interpreters.Select(i => i.Name).ToList() };
        writer.SelectedIndex = Math.Max(0, interpreters.FindIndex(i => i.IsDefault));
        string? Chosen() => writer.SelectedIndex >= 0 && writer.SelectedIndex < interpreters.Count ? interpreters[writer.SelectedIndex].Name : null;
        Label summary = Ui.Text("");
        Label theory = Ui.Muted("");
        VerticalStackLayout conversation = Ui.Stack(8);
        Entry question = new() { Placeholder = "Ask about this song (e.g. why does the chorus feel brighter?)" };

        async Task StreamAsync(IAsyncEnumerable<InterpretationEvent> events, Label into, Action<InterpretationEvent>? done)
        {
            try
            {
                await foreach (InterpretationEvent e in events)
                {
                    if (e.Restart)
                    {
                        into.Text = "";
                    }

                    if (e.Text is { } words)
                    {
                        into.Text += words;
                    }

                    if (e.Summary is not null || e.Answer is not null)
                    {
                        done?.Invoke(e);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                into.Text = $"Couldn't write it: {ex.Message}";
            }
            catch (OperationCanceledException)
            {
            }
        }

        async Task WriteSummaryAsync()
        {
            _asking?.Cancel();
            _asking = new CancellationTokenSource();
            summary.Text = "";
            theory.Text = "";
            await StreamAsync(_api.StreamInterpretationAsync(JobId, Chosen(), _asking.Token), summary, e =>
            {
                if (e.Summary is { } s)
                {
                    summary.Text = s.Text;
                    theory.Text = s.TheoryText is null ? "" : $"For musicians: {s.TheoryText}";
                }
            });
        }

        async Task AskOneAsync()
        {
            string q = question.Text?.Trim() ?? "";
            if (q.Length < 3)
            {
                return;
            }

            question.Text = "";
            Label answer = Ui.Text("");
            conversation.Add(Ui.Card(Ui.Stack(4, Ui.Colored(q, Ui.Accent, 14), answer)));
            _asking?.Cancel();
            _asking = new CancellationTokenSource();
            await StreamAsync(_api.StreamAnswerAsync(JobId, q, [.. _history], Chosen(), _asking.Token), answer, e =>
            {
                if (e.Answer is { } a)
                {
                    answer.Text = a.Grounded ? a.Answer : $"{a.Answer}\n(Couldn't check every figure against the measurements.)";
                    _history.Add(new InterpretationTurn(q, a.Answer));
                }
            });
        }

        View view = Ui.Stack(10,
            interpreters.Count > 1 ? writer : Ui.Muted(""),
            Ui.Secondary("Write it again", WriteSummaryAsync),
            summary,
            theory,
            Ui.H2("Ask"),
            conversation,
            question,
            Ui.Primary("Ask", AskOneAsync));
        _ = WriteSummaryAsync();
        return view;
    }

    private View More()
    {
        VerticalStackLayout v = Ui.Stack(10);
        v.Add(Ui.Secondary("Share the MIDI", async () =>
        {
            if (await _api.DownloadAsync($"api/analysis/{JobId}/midi", $"{Path.GetFileNameWithoutExtension(_status?.FileName ?? "song")}.mid") is { } path)
            {
                await Share.Default.RequestAsync(new ShareFileRequest { Title = "MIDI", File = new ShareFile(path, "audio/midi") });
            }
            else
            {
                await Ui.Toast("No MIDI for this song.");
            }
        }));
        v.Add(Ui.H2("Try another model"));
        v.Add(Ui.Muted("Run one step again with a different model, to compare. It starts a new analysis."));
        v.Add(Ui.Secondary("Choose…", RerunAsync));
        v.Add(Ui.H2("Analysed with"));
        foreach (StageRecord r in _status?.StageHistory ?? [])
        {
            v.Add(Ui.Muted($"{r.Stage}: {r.Executor} ({r.Tier})"));
        }

        v.Add(Ui.Danger("Delete this song", DeleteAsync));
        return v;
    }

    private async Task RerunAsync()
    {
        List<StageExecutorsDto> stages = await _api.GetExecutorsAsync() ?? [];
        string stage = await DisplayActionSheetAsync("Which step?", "Cancel", null, [.. stages.Select(s => s.Stage)]);
        StageExecutorsDto? chosen = stages.Find(s => s.Stage == stage);
        if (chosen is null)
        {
            return;
        }

        string executor = await DisplayActionSheetAsync($"{stage} with", "Cancel", null, [.. chosen.Executors.Where(e => e.Available).Select(e => e.Name)]);
        if (!chosen.Executors.Any(e => e.Name == executor))
        {
            return;
        }

        var (status, error) = await _api.RerunAsync(JobId, stage, executor);
        if (status is null)
        {
            await Ui.Toast(error ?? "The server refused.");
            return;
        }

        await Shell.Current.GoToAsync($"song?id={status.JobId}");
    }

    private async Task DeleteAsync()
    {
        if (!await DisplayAlertAsync("Delete", "Delete this song and its analysis?", "Delete", "Keep"))
        {
            return;
        }

        if (await _api.DeleteSongAsync(JobId))
        {
            await Shell.Current.GoToAsync("..");
        }
        else
        {
            await Ui.Toast("It couldn't be deleted (busy, or gone already).");
        }
    }

    public void Dispose()
    {
        _vocals?.Dispose();
        _backing?.Dispose();
        _asking?.Dispose();
    }
}
