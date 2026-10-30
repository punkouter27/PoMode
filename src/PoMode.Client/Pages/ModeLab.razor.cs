using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using PoMode.Client.Components.ModeLab;
using PoMode.Client.Services;
using PoMode.Shared.Analysis;

namespace PoMode.Client.Pages;

public partial class ModeLab
{
    private static readonly IReadOnlyList<MelodyStyle> StyleOptions =
    [
        MelodyStyle.Lyrical,
        MelodyStyle.Arpeggiated,
        MelodyStyle.Syncopated,
        MelodyStyle.Motific
    ];

    /// <summary>The loop as files, built from the same request the preview plays.</summary>
    private IReadOnlyList<DownloadMenu.Item> DownloadItems
    {
        get
        {
            var query = $"TonicPitchClass={_selectedTonic}&Mode={_selectedMode}&ProgressionId={Uri.EscapeDataString(_selectedProgressionId)}&Bpm={_bpm}&Style={_selectedStyle}&Seed={_seed}&TargetPurity={_targetPurity}";
            return
            [
                new("WAV audio (44.1 kHz stereo)", $"api/modal-melodies/wav?{query}", "Download the loop as a WAV file"),
                new("MIDI (melody and chords)", $"api/modal-melodies/midi?{query}", "Download the loop as a standard MIDI file"),
            ];
        }
    }

    private List<ChordProgressionDefinition> _progressions = [];
    private int _selectedTonic = 0; // C
    private string _selectedProgressionId = "pop-axis";
    private ScaleMode _selectedMode = ScaleMode.Ionian;
    private MelodyStyle _selectedStyle = MelodyStyle.Lyrical;
    private double _targetPurity = 90.0;
    private double _bpm = 100.0;
    // The tempo the picked beat was played at, so the tempo slider detents on it, its half and double.
    private double? _beatBpm;
    private int _seed = 42;
    private bool _matchHarmonyToMode = true;

    private GeneratedMelodyDto? _melody;
    private bool _isLoading = true;
    private bool _isPlaying;
    private bool _isLooping = true;
    private bool _isSendingToAnalyzer;
    private double _playheadSec;
    private double _totalDurationSec = 8.0;
    private int? _activeWindowIndex;

    private IJSObjectReference? _playerModule;
    private IJSObjectReference? _sfx;
    private IJSObjectReference? _midi;
    private DrumStrip? _drums;
    private GravityPanel? _gravity;
    private bool _midiSupported;
    private List<string>? _midiInputs;
    private int _midiNotes;
    private int _midiOutside;

    /// <summary>The library job to open as the drums, set by the Library's "Choose a beat".</summary>
    [SupplyParameterFromQuery] public string? Beat { get; set; }

    /// <summary>A song page's "Explore it here": the mode to open on (<see cref="ModeLink"/>).</summary>
    [SupplyParameterFromQuery] public int? Tonic { get; set; }

    [SupplyParameterFromQuery] public string? Mode { get; set; }

    /// <summary>A song page's loop (<see cref="SongLoops"/>): the lab opens on the song's own chords.</summary>
    [SupplyParameterFromQuery] public string? Loop { get; set; }

    private DotNetObjectReference<ModeLab>? _selfRef;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                _midi = await JS.InvokeAsync<IJSObjectReference>("import", "./js/player/midi-in.js");
                _midiSupported = await _midi.InvokeAsync<bool>("supported");
                if (_midiSupported)
                {
                    StateHasChanged();
                }
            }
            await SyncSfxPaletteAsync();
        }
        catch (JSDisconnectedException)
        {
            // Navigated away mid-render.
        }
    }

    private (int Tonic, ScaleMode Mode)? _paletteSent;

    /// <summary>The interface's own sounds (the chimes and taps) are voiced in the mode on screen, as
    /// they are in the song's key on an analysis page.</summary>
    private async Task SyncSfxPaletteAsync()
    {
        if (_paletteSent == (_selectedTonic, _selectedMode))
        {
            return;
        }
        _paletteSent = (_selectedTonic, _selectedMode);
        _sfx ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/shell/sfx.js");
        await _sfx.InvokeVoidAsync("setPalette", ModeNames.Pitches(_selectedTonic, _selectedMode));
    }

    private string MidiTitle => _midiInputs switch
    {
        null => "Play along on a MIDI keyboard: notes sound with the loop and light on the home-note view",
        { Count: 0 } => "Connected, but no MIDI keyboard is plugged in",
        var names => $"Listening to {string.Join(", ", names)}. Press again to stop.",
    };

    private string MidiReadout => _midiInputs is null || _midiNotes == 0
        ? ""
        : $" · {_midiNotes} {(_midiNotes == 1 ? "note" : "notes")} played, {_midiOutside} outside the mode";

    private async Task ToggleMidiAsync()
    {
        if (_midi is null)
        {
            return;
        }
        if (_midiInputs is not null)
        {
            await _midi.InvokeVoidAsync("disconnect");
            _midiInputs = null;
            return;
        }
        _selfRef ??= DotNetObjectReference.Create(this);
        var names = await _midi.InvokeAsync<string[]?>("connect", _selfRef, _gravity?.Canvas);
        if (names is null)
        {
            NotificationService.Notify(NotificationSeverity.Warning, "MIDI", "This browser would not open its MIDI keyboards.");
            return;
        }
        (_midiInputs, _midiNotes, _midiOutside) = ([.. names], 0, 0);
    }

    [JSInvokable]
    public void OnMidiInputs(string[] names)
    {
        if (_midiInputs is not null)
        {
            _midiInputs = [.. names];
            StateHasChanged();
        }
    }

    [JSInvokable]
    public void OnMidiNote(int midi, bool inMode)
    {
        _midiNotes++;
        if (!inMode)
        {
            _midiOutside++;
        }
        StateHasChanged();
    }

    private string? _description;
    private string? _describeSentence;
    private bool _describing;

    /// <summary>Applies a described change through the same paths the controls use, so "Match to mode"
    /// still moves the harmony with a new mode; the described tempo is set after it.</summary>
    private async Task DescribeAsync()
    {
        if (string.IsNullOrWhiteSpace(_description) || _describing)
        {
            return;
        }
        _describing = true;
        try
        {
            if (await Analysis.DescribeModeLabAsync(_description, CurrentRequest()) is not { } described)
            {
                _describeSentence = "That could not be read just now.";
                return;
            }
            _describeSentence = described.Sentence;
            if (!described.Changed)
            {
                return;
            }
            var next = described.Request;
            if (next.Mode != _selectedMode)
            {
                _selectedMode = next.Mode;
                ApplyModeHarmony(next.Mode);
            }
            _selectedTonic = next.TonicPitchClass;
            _selectedStyle = next.Style;
            _targetPurity = next.TargetPurity;
            _seed = next.Seed;
            if (Math.Abs(next.Bpm - CurrentRequest().Bpm) > 0.5 || described.Sentence.Contains("BPM", StringComparison.Ordinal))
            {
                _bpm = next.Bpm;
            }
            _description = null;
            await OnConfigChanged();
        }
        catch (HttpRequestException)
        {
            _describeSentence = "The server could not be reached.";
        }
        finally
        {
            _describing = false;
        }
    }

    /// <summary>A newly picked beat sets the tempo it was played at; the drums are then cut to it.</summary>
    private async Task OnBeatTempoAsync(double bpm)
    {
        _bpm = bpm;
        _beatBpm = bpm;
        await RefreshMelodyAsync();
        StateHasChanged();
    }

    protected override async Task OnInitializedAsync()
    {
        await Flags.LoadAsync();
        try
        {
            var progs = await Analysis.GetModalProgressionsAsync();
            if (progs is { Count: > 0 })
            {
                _progressions = progs;
                _selectedProgressionId = _progressions[0].Id;
                _bpm = _progressions[0].DefaultBpm;
                if (ModeLink.TryRead(Tonic, Mode, out var parent, out var linked))
                {
                    (_selectedTonic, _selectedMode) = (parent, linked);
                }

                if (SongLoops.Definition(Loop) is { } songLoop)
                {
                    // The song's chords stay put while mode cards are tried over them, so the match
                    // starts off: switched on, the first card would swap them for its own cadence.
                    _progressions.Insert(0, songLoop);
                    (_selectedProgressionId, _bpm, _matchHarmonyToMode) = (songLoop.Id, songLoop.DefaultBpm, false);
                }
                // Open on the harmony that matches the mode the strip starts on, so the first thing
                // heard is already the mode rather than the parent key.
                ApplyModeHarmony(_selectedMode);
                if (Beat is { Length: > 0 })
                {
                    // Arriving with a beat: 120 only until the recording has been read, and as the
                    // hint for which note value its pulse is. The beat's own tempo replaces it.
                    _bpm = DrumStrip.StartBpm;
                }
            }
            await RefreshMelodyAsync();
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Initialization Error", ex.Message);
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>The selectors as the server's request shape — one copy, so the preview, the analyzer
    /// hand-off and a hum take can never be generated from subtly different settings.</summary>
    private ModalMelodyRequest CurrentRequest() => new(
        TonicPitchClass: _selectedTonic,
        Mode: _selectedMode,
        ProgressionId: _selectedProgressionId,
        Bpm: _bpm,
        Style: _selectedStyle,
        Seed: _seed,
        TargetPurity: _targetPurity);

    private async Task RefreshMelodyAsync()
    {
        var request = CurrentRequest();
        GeneratedMelodyDto? melody;
        try
        {
            melody = await Analysis.GenerateModalMelodyAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            // The server restarting under an open lab: say so and keep the loop that is on screen.
            NotificationService.Notify(NotificationSeverity.Warning, "Mode Lab", "The server could not be reached. Try again in a moment.");
            return;
        }
        if (request != CurrentRequest())
        {
            return; // superseded by a later slider step; its own refresh will land
        }
        _melody = melody;
        if (_melody is not null)
        {
            _totalDurationSec = _melody.MelodyNotes.Count > 0
                ? Math.Max(_melody.MelodyNotes.Max(n => n.StartSec + n.DurationSec), _melody.Chords.Max(c => c.EndSec))
                : 8.0;

            if (_isPlaying && _playerModule is not null)
            {
                await _playerModule.InvokeVoidAsync("updateMelody", _melody.MelodyNotes, _melody.BackingNotes, _totalDurationSec);
            }
        }
    }

    /// <summary>
    /// Points the harmony at the selected mode's own signature cadence. A mode is a tonal centre, not
    /// just a note set: over a progression that keeps insisting on the parent key, every mode comes out
    /// sounding like that key with the melody starting somewhere else.
    /// </summary>
    /// <summary>
    /// Moves the backing chords onto the selected mode's own signature.
    /// </summary>
    /// <remarks>
    /// The chords are the point of the setting — picking Lydian and hearing Lydian's cadence is the
    /// lab working. The tempo used to move with them, and that was the surprise: a 150 BPM loop set
    /// up for practising was gone after clicking "Phrygian", and the tempo slider appeared to jump
    /// by itself every time a card was pressed. Speed is a practice decision the user makes with the
    /// slider beside it, so it stays put.
    /// </remarks>
    private void ApplyModeHarmony(ScaleMode mode)
    {
        if (!_matchHarmonyToMode) return;

        var signature = _progressions.SignatureFor(mode);
        if (signature is null || signature.Id == _selectedProgressionId) return;

        _selectedProgressionId = signature.Id;
    }

    private async Task SampleModeAsync(ScaleMode mode)
    {
        _selectedMode = mode;
        ApplyModeHarmony(mode);
        await RefreshMelodyAsync();

        // Always restart the picked mode from its first beat. Falling through to updateMelody while
        // a take is in flight kept the previous mode's chords and loop length, and resuming on a
        // stale pause offset dropped the new sample mid-phrase.
        await StopAsync();
        await PlayAsync();
    }

    private async Task OnConfigChanged(object? _ = null)
    {
        await RefreshMelodyAsync();
        if (_isPlaying)
        {
            // The arrangement just changed underneath the playhead, so this is a new take.
            await StopAsync();
            await PlayAsync();
        }
    }

    private async Task OnTonicPickedAsync(int tonic)
    {
        _selectedTonic = tonic;
        await OnConfigChanged();
    }

    private async Task OnProgressionPickedAsync(string id)
    {
        _selectedProgressionId = id;
        await OnProgressionChanged();
    }

    private async Task OnTempoPickedAsync(double bpm)
    {
        _bpm = bpm;
        await OnTempoChanged();
    }

    private async Task OnPurityChanged(double _ = 0)
    {
        // The slider hands back negative zero at the bottom of its travel, which formats as "-0%".
        if (_targetPurity <= 0) _targetPurity = 0;

        await RefreshMelodyAsync();
        if (_isPlaying)
        {
            // The arrangement just changed underneath the playhead, so this is a new take.
            await StopAsync();
            await PlayAsync();
        }
    }

    private async Task OnProgressionChanged(object? _ = null)
    {
        var prog = _progressions.FirstOrDefault(p => p.Id == _selectedProgressionId);
        if (prog is not null)
        {
            _bpm = prog.DefaultBpm;
            _selectedMode = prog.SuggestedMode;
        }
        await RefreshMelodyAsync();
        if (_isPlaying)
        {
            // The arrangement just changed underneath the playhead, so this is a new take.
            await StopAsync();
            await PlayAsync();
        }
    }

    private async Task OnTempoChanged(double _ = 0)
    {
        await RefreshMelodyAsync();
        if (_isPlaying)
        {
            // The arrangement just changed underneath the playhead, so this is a new take.
            await StopAsync();
            await PlayAsync();
        }
    }

    private async Task RandomizeSeedAsync()
    {
        _seed = Random.Shared.Next(1, 100000);
        await RefreshMelodyAsync();

        // A new melody is a new take, so it starts from bar one. Swapping the notes underneath a
        // running playhead left it partway through a phrase that no longer existed.
        var wasPlaying = _isPlaying;
        await StopAsync();
        if (wasPlaying)
        {
            await PlayAsync();
        }
    }

    /// <summary>
    /// Rolls a different progression to sing over. Deliberately leaves the key alone: a singer picks
    /// a key for their own range and does not want it moving under them, whereas the chords are the
    /// thing worth re-rolling until one sounds worth a take.
    ///
    /// <para>Sets the mode from the progression the way the dropdown does, and never routes through
    /// <see cref="ApplyModeHarmony"/> — that would look up the new mode's signature cadence and snap
    /// the harmony straight back off the roll.</para>
    /// </summary>
    private async Task RandomizeProgressionAsync()
    {
        if (_progressions.Count < 2) return;

        // Re-roll rather than allow a no-op: a dice button that lands on the same progression reads
        // as broken, and with a catalog this size that happens often.
        ChordProgressionDefinition next;
        do
        {
            next = _progressions[Random.Shared.Next(_progressions.Count)];
        }
        while (next.Id == _selectedProgressionId);

        _selectedProgressionId = next.Id;
        _bpm = next.DefaultBpm;
        _selectedMode = next.SuggestedMode;

        // A fresh progression deserves a fresh melody over it — the old seed's phrases were shaped
        // around chords that are gone.
        _seed = Random.Shared.Next(1, 100000);

        // A roll that lands while a take is already captured would leave "Save my melody" pointing
        // at chords that are no longer on screen. The take itself still carries its own backing, but
        // the screen would be lying about what it was.
        await RefreshMelodyAsync();

        // Audition it straight away: the whole point of the roll is to hear whether it is worth
        // singing over, and from bar one rather than wherever the previous loop had got to.
        var wasPlaying = _isPlaying;
        await StopAsync();
        if (wasPlaying)
        {
            await PlayAsync();
        }
    }

    private async Task SendToAnalyzerAsync()
    {
        if (_melody is null || _isSendingToAnalyzer) return;
        _isSendingToAnalyzer = true;
        try
        {
            if (_isPlaying)
            {
                await PauseAsync();
            }

            var job = await Analysis.AnalyzeModalMelodyAsync(CurrentRequest());
            if (job is not null && !string.IsNullOrEmpty(job.JobId))
            {
                NotificationService.Notify(NotificationSeverity.Success, "Mode Lab Bridge", "Synthesized WAV audio & queued analysis job!");
                Navigation.NavigateTo($"/song/{job.JobId}");
            }
            else
            {
                NotificationService.Notify(NotificationSeverity.Error, "Analysis Error", "Failed to queue analysis job.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Error", ex.Message);
        }
        finally
        {
            _isSendingToAnalyzer = false;
        }
    }

    private async Task EnsurePlayerAsync()
    {
        _playerModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/player/modal-player.js");
        _selfRef ??= DotNetObjectReference.Create(this);
    }

    private async Task PlayAsync()
    {
        if (_melody is null) return;
        await EnsurePlayerAsync();
        if (_playerModule is null) return;

        _isPlaying = true;
        await _playerModule.InvokeVoidAsync("play", _melody.MelodyNotes, _melody.BackingNotes, _totalDurationSec, _selfRef);
        await SyncDrumsAsync();
    }

    private async Task PauseAsync()
    {
        if (_playerModule is null) return;
        _isPlaying = false;
        await _playerModule.InvokeVoidAsync("pause");
        await SyncDrumsAsync();
    }

    private async Task StopAsync()
    {
        if (_playerModule is null) return;
        _isPlaying = false;
        _playheadSec = 0;
        _activeWindowIndex = null;
        await _playerModule.InvokeVoidAsync("stop");
        await SyncDrumsAsync();
    }

    /// <summary>Tells the drum loop the transport moved (DrumStrip.SyncAsync).</summary>
    private Task SyncDrumsAsync() => _drums?.SyncAsync() ?? Task.CompletedTask;

    private async Task ToggleMatchHarmonyAsync()
    {
        _matchHarmonyToMode = !_matchHarmonyToMode;
        if (_matchHarmonyToMode)
        {
            ApplyModeHarmony(_selectedMode);
        }
        else if (_progressions.FirstOrDefault(p => p.Id == _selectedProgressionId)?.RootsOn == HarmonicRoot.ModeRoot)
        {
            // Turning the match off is a request for one harmony that does not follow the mode, so a
            // mode-rooted progression has to give way to a parent-rooted one or nothing would change.
            var shared = _progressions.FirstSharedHarmony();
            if (shared is not null)
            {
                _selectedProgressionId = shared.Id;
                _bpm = shared.DefaultBpm;
            }
        }
        await RefreshMelodyAsync();
        if (_isPlaying)
        {
            await StopAsync();
            await PlayAsync();
        }
    }

    private async Task ToggleLoopAsync()
    {
        _isLooping = !_isLooping;
        if (_playerModule is not null)
        {
            await _playerModule.InvokeVoidAsync("setLooping", _isLooping);
        }
    }

    private async Task OnSeek(double seekSec)
    {
        _playheadSec = seekSec;
        UpdateActiveWindow(seekSec);
        if (_playerModule is not null)
        {
            await _playerModule.InvokeVoidAsync("seek", seekSec);
            await SyncDrumsAsync();
        }
    }

    /// <summary>Raised by the player when a non-looping take runs off its end.</summary>
    [JSInvokable]
    public async Task OnPlaybackEnded()
    {
        _isPlaying = false;
        _playheadSec = 0;
        _activeWindowIndex = null;
        StateHasChanged();
        await SyncDrumsAsync();
    }

    [JSInvokable]
    public void OnPlayheadUpdated(double positionSec)
    {
        _playheadSec = positionSec;
        UpdateActiveWindow(positionSec);
        StateHasChanged();
    }

    private void UpdateActiveWindow(double pos)
    {
        if (_melody?.ModalAnalysis is { } result)
        {
            _activeWindowIndex = result.WindowIndexAt(pos);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_drums is not null)
            {
                // Before the player: the loop sounds on the player's context.
                await _drums.DisposeAsync();
            }
            if (_midi is not null)
            {
                await _midi.InvokeVoidAsync("disconnect");
                await _midi.DisposeAsync();
            }
            if (_sfx is not null)
            {
                // Back to the keyless defaults: the Library should not click in the lab's mode.
                await _sfx.InvokeVoidAsync("setPalette", (int[]?)null);
                await _sfx.DisposeAsync();
            }
            if (_playerModule is not null)
            {
                await _playerModule.InvokeVoidAsync("dispose");
                await _playerModule.DisposeAsync();
            }
            _selfRef?.Dispose();
        }
        catch (JSDisconnectedException) { }
    }
}
