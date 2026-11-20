using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;

namespace PoMode.ApiClient;

/// <summary>
/// Every call the web client and the phone app make to PoMode's API, against one set of routes,
/// through the shared source-generated <see cref="PoModeJsonContext"/> (so neither client serializes by
/// reflection, and neither can disagree with the server on a field). The web client uses it as is,
/// relative to the page's origin; the phone subclasses it to point at a server and keep its cookie.
/// </summary>
public class AnalysisClient(HttpClient http)
{
    private static PoModeJsonContext Json => PoModeJsonContext.Default;

    /// <summary>The HTTP client every call goes through, for a subclass's own calls.</summary>
    protected HttpClient Http => http;

    /// <summary>Applied to each streaming request before it is sent. The browser buffers a fetch's
    /// whole body unless streaming is switched on per request, so the web client sets this.</summary>
    public Action<HttpRequestMessage>? PrepareStreaming { get; init; }

    /// <summary>Where a route lives. Relative (to the HttpClient's base address) unless a subclass
    /// knows the server another way.</summary>
    public virtual Uri Url(string relative) => new(relative, UriKind.Relative);

    // ---- One analysis ----------------------------------------------------------------------------

    public Task<JobStatusDto?> GetStatusAsync(string jobId, CancellationToken ct = default)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}"), Json.JobStatusDto, ct);

    /// <summary>Requests cancellation of a running job; progress updates arrive over SignalR.</summary>
    public async Task CancelAsync(string jobId)
    {
        using var _ = await http.DeleteAsync(Url($"api/analysis/{jobId}"));
    }

    public Task<ModalResult?> GetResultAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}/result"), Json.ModalResult);

    /// <summary>The separated stems this job has ("vocals", "instrumental"); empty when separation was skipped.</summary>
    public async Task<string[]> GetStemsAsync(string jobId)
        => await http.GetFromJsonAsync(Url($"api/analysis/{jobId}/stems"), Json.StringArray) ?? [];

    /// <summary>The fast first-look estimate, or null while it hasn't been written yet.</summary>
    public Task<AnalysisPreviewDto?> GetPreviewAsync(string jobId)
        => SuccessOrNullAsync($"api/analysis/{jobId}/preview", Json.AnalysisPreviewDto);

    /// <summary>The canvas payload — note roles and labels already decided server-side.</summary>
    public Task<VisualizationPayload?> GetVisualAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}/visual"), Json.VisualizationPayload);

    /// <summary>The similarity matrix and novelty curve the section ribbon was cut from, or null when
    /// the song did not divide into sections (the server answers 404, as the ribbon is absent then).</summary>
    public Task<SongStructureDto?> GetStructureAsync(string jobId)
        => SuccessOrNullAsync($"api/analysis/{jobId}/structure", Json.SongStructureDto);

    /// <summary>The lead singer's voice type and strongest note. The first request for a song measures
    /// its vocal, so it can take a second or two; later ones are cached server-side.</summary>
    public Task<VoiceProfileDto?> GetVoiceProfileAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}/voice"), Json.VoiceProfileDto);

    /// <summary>Every derived song/melody statistic, plus the fingerprint paragraph.</summary>
    public Task<SongStats?> GetStatsAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}/stats"), Json.SongStats);

    /// <summary>Seconds the job has left, or null when the server has no timing to base it on.</summary>
    public async Task<double?> GetEtaAsync(string jobId)
        => (await http.GetFromJsonAsync(Url($"api/analysis/{jobId}/eta"), Json.EtaDto))?.SecondsLeft;

    /// <summary>The song's chords as a player fingers them, with the capo advice; null when it has none.</summary>
    public Task<ChordShapesDto?> GetChordShapesAsync(string jobId)
        => SuccessOrNullAsync($"api/analysis/{jobId}/shapes", Json.ChordShapesDto);

    /// <summary>The chord cycle the song repeats most; null when it never comes round twice.</summary>
    public Task<SongLoopDto?> GetSongLoopAsync(string jobId)
        => OkOrNullAsync($"api/analysis/{jobId}/loop", Json.SongLoopDto);

    /// <summary>Published songs on the same chords (Hooktheory); null when it could not be asked.</summary>
    public Task<List<ProgressionSongDto>?> GetSongsOnLoopAsync(string jobId)
        => SuccessOrNullAsync($"api/analysis/{jobId}/loop/songs", Json.ListProgressionSongDto);

    /// <summary>The lyrics, timed; null until aftercare has written them, or when there were none.</summary>
    public Task<List<LyricLineDto>?> GetLyricsAsync(string jobId)
        => OkOrNullAsync($"api/analysis/{jobId}/lyrics", Json.ListLyricLineDto);

    /// <summary>How an analysed song sits against the caller's own range, or null when the server has
    /// nothing to say (too few takes, or the job is itself a take).</summary>
    public Task<SongVoiceFitDto?> GetSongVoiceFitAsync(string jobId)
        => OkOrNullAsync($"api/modal-melodies/voice/song/{jobId}", Json.SongVoiceFitDto);

    /// <summary>The selectable executors per pipeline stage, in the planner's own order.</summary>
    public Task<List<StageExecutorsDto>?> GetExecutorsAsync()
        => http.GetFromJsonAsync(Url("api/analysis/executors"), Json.ListStageExecutorsDto);

    /// <summary>A new run of a finished analysis from <paramref name="stage"/> on, with that stage
    /// pinned to <paramref name="executor"/>; null with the server's reason when refused.</summary>
    public async Task<(JobStatusDto? Status, string? Error)> RerunAsync(string jobId, string stage, string executor)
    {
        using var response = await http.PostAsync(
            Url($"api/analysis/{jobId}/rerun?stage={Uri.EscapeDataString(stage)}&executor={Uri.EscapeDataString(executor)}"), content: null);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(Json.JobStatusDto), null)
            : (null, await ReasonAsync(response));
    }

    // ---- The written interpretation (server-sent events) -----------------------------------------

    /// <summary>The interpreters this server can run, with live availability.</summary>
    public Task<List<InterpreterOptionDto>?> GetInterpretersAsync()
        => http.GetFromJsonAsync(Url("api/analysis/interpreters"), Json.ListInterpreterOptionDto);

    /// <summary>
    /// The written summary of the statistics, streamed as it is written. <paramref name="interpreter"/>
    /// names one explicitly; omitting it takes the server's default. The last event carries the
    /// finished, checked summary.
    /// </summary>
    public IAsyncEnumerable<InterpretationEvent> StreamInterpretationAsync(
        string jobId, string? interpreter, CancellationToken ct = default)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Get, Url($"api/analysis/{jobId}/interpretation"
            + (string.IsNullOrWhiteSpace(interpreter) ? "" : $"?interpreter={Uri.EscapeDataString(interpreter)}"))), ct);

    /// <summary>
    /// Asks a follow-up about the same measurements, streamed like the summary.
    /// <paramref name="history"/> is the conversation so far — the server keeps none, so the client is
    /// what makes it a conversation at all.
    /// </summary>
    public IAsyncEnumerable<InterpretationEvent> StreamAnswerAsync(
        string jobId, string question, IReadOnlyList<InterpretationTurn> history, string? interpreter,
        CancellationToken ct = default)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Post, Url($"api/analysis/{jobId}/interpretation/ask"))
        {
            Content = JsonContent.Create(new SongQuestionRequest(question, history, interpreter), Json.SongQuestionRequest),
        }, ct);

    private async IAsyncEnumerable<InterpretationEvent> StreamAsync(
        HttpRequestMessage request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = request;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        PrepareStreaming?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => "the analysis for this song is no longer available.",
                HttpStatusCode.TooManyRequests => "that is more requests than the server takes in a minute. Wait a moment and try again.",
                var status => $"the server answered {(int)status}.",
            });
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(ct))
        {
            if (JsonSerializer.Deserialize(item.Data, Json.InterpretationEvent) is { } parsed)
            {
                yield return parsed;
            }
        }
    }

    // ---- Library and account ---------------------------------------------------------------------

    /// <summary>Every persisted job, newest first, with headline analysis for completed ones.</summary>
    public Task<List<LibraryEntryDto>?> GetLibraryAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync(Url("api/library"), Json.ListLibraryEntryDto, ct);

    /// <summary>The caller's other songs most like this one, with the reason in words.</summary>
    public Task<List<SimilarSongDto>?> GetSimilarAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/library/{jobId}/similar"), Json.ListSimilarSongDto);

    /// <summary>The caller's songs that sound like the words, best first; null when this server cannot
    /// search by sound at all (Azure, or the CLAP models not downloaded yet).</summary>
    public Task<List<SoundMatchDto>?> SearchBySoundAsync(string words)
        => SuccessOrNullAsync($"api/library/sound?q={Uri.EscapeDataString(words)}", Json.ListSoundMatchDto);

    /// <summary>Deletes one of the caller's songs; false when the server could not (gone already, or busy).</summary>
    public async Task<bool> DeleteSongAsync(string jobId)
    {
        using var response = await http.DeleteAsync(Url($"api/library/{jobId}"));
        return response.IsSuccessStatusCode;
    }

    /// <summary>Deletes every job the caller owns; answers how many went.</summary>
    public async Task<int> DeleteMyDataAsync()
    {
        using var response = await http.DeleteAsync(Url("api/account/data"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(Json.Int32);
    }

    public Task<DiagnosticsReport?> GetDiagnosticsAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync(Url("api/diag"), Json.DiagnosticsReport, ct);

    // ---- Starting analyses -----------------------------------------------------------------------

    /// <summary>
    /// Starts analysing a finished resumable upload, with this client's Tier-2 capability and the
    /// per-stage model picks on the query string (keys from the shared
    /// <see cref="StageNames.ExecutorQueryKeys"/> table; Auto sends nothing). The phone never claims
    /// <paramref name="clientCanInfer"/>: it cannot run the browser's pitch tracker.
    ///
    /// <para>A dropped connection is retried: the server answers a repeat for the same upload with the
    /// job the first call started, so a response lost on a flaky network does not strand the file.
    /// Returns the server's reason when it refuses.</para>
    /// </summary>
    public async Task<(JobStatusDto? Status, string? Error)> StartFromUploadAsync(
        string uploadId, bool clientCanInfer, IReadOnlyDictionary<string, string> executorChoices, bool asBeat = false,
        CancellationToken ct = default)
    {
        var query = new List<string>(4);
        if (asBeat)
        {
            // Kept for the Mode Lab's drum loop, never analysed: model picks would mean nothing.
            query.Add("beat=true");
        }
        if (clientCanInfer)
        {
            query.Add("clientCanInfer=true");
        }
        foreach (var (stage, queryKey) in StageNames.ExecutorQueryKeys)
        {
            if (executorChoices.TryGetValue(stage, out var name))
            {
                query.Add($"{queryKey}={Uri.EscapeDataString(name)}");
            }
        }
        var url = Url($"api/analysis/uploads/{Uri.EscapeDataString(uploadId)}"
            + (query.Count == 0 ? "" : "?" + string.Join("&", query)));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.PostAsync(url, content: null, ct);
                return response.IsSuccessStatusCode
                    ? (await response.Content.ReadFromJsonAsync(Json.JobStatusDto, ct), null)
                    : (null, await ReasonAsync(response));
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    // ---- Mode Lab and Practice -------------------------------------------------------------------

    /// <summary>Standard chord progression presets across genres and modes.</summary>
    public Task<List<ChordProgressionDefinition>?> GetModalProgressionsAsync()
        => http.GetFromJsonAsync(Url("api/modal-melodies/progressions"), Json.ListChordProgressionDefinition);

    /// <summary>Generates an algorithmic melody and chord accompaniment for a scale mode and progression.</summary>
    public async Task<GeneratedMelodyDto?> GenerateModalMelodyAsync(ModalMelodyRequest request)
    {
        using var response = await http.PostAsJsonAsync(Url("api/modal-melodies/generate"), request, Json.ModalMelodyRequest);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.GeneratedMelodyDto) : null;
    }

    /// <summary>The Mode Lab's settings from a description such as "darker and slower".</summary>
    public async Task<ModeLabDescriptionDto?> DescribeModeLabAsync(string text, ModalMelodyRequest current)
    {
        using var response = await http.PostAsJsonAsync(Url("api/modal-melodies/describe"),
            new ModeLabDescriptionRequest(text, current), Json.ModeLabDescriptionRequest);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.ModeLabDescriptionDto) : null;
    }

    /// <summary>Synthesizes the melody and chords into WAV audio and queues an end-to-end analysis job.</summary>
    public async Task<JobStatusDto?> AnalyzeModalMelodyAsync(ModalMelodyRequest request)
    {
        using var response = await http.PostAsJsonAsync(Url("api/modal-melodies/analyze"), request, Json.ModalMelodyRequest);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.JobStatusDto) : null;
    }

    /// <summary>The backing's query string, as the WAV and MIDI exports and the hum upload all read
    /// it, so the server can regenerate the exact progression that was heard.</summary>
    public static string BackingQuery(ModalMelodyRequest backing) =>
        string.Create(CultureInfo.InvariantCulture,
            $"tonicPitchClass={backing.TonicPitchClass}&mode={backing.Mode}&progressionId={Uri.EscapeDataString(backing.ProgressionId)}"
            + $"&bpm={backing.Bpm}&style={backing.Style}&seed={backing.Seed}&targetPurity={backing.TargetPurity}");

    /// <summary>
    /// Uploads a hum take together with the backing it was sung over. A solo hum contains no harmony
    /// for a chord recognizer to find, so the server lays the backing's own chords under the melody.
    /// </summary>
    public async Task<JobStatusDto?> UploadHumTakeAsync(byte[] wavBytes, ModalMelodyRequest backing)
    {
        using var content = WavForm(wavBytes, "hum.wav");
        using var response = await http.PostAsync(Url($"api/modal-melodies/hum?{BackingQuery(backing)}"), content);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.JobStatusDto) : null;
    }

    /// <summary>The caller's earlier takes over this backing's mode and progression. Key and tempo are
    /// not sent: the server matches takes across them on purpose.</summary>
    public Task<TakeHistoryDto?> GetTakeHistoryAsync(ModalMelodyRequest backing)
        => http.GetFromJsonAsync(Url(string.Create(CultureInfo.InvariantCulture,
            $"api/modal-melodies/takes?mode={backing.Mode}&progressionId={Uri.EscapeDataString(backing.ProgressionId)}&targetPurity={backing.TargetPurity}")),
            Json.TakeHistoryDto);

    /// <summary>The caller's sung range, and the key that fits <paramref name="mode"/> to it once
    /// there are enough takes to say.</summary>
    public Task<VocalRangeDto?> GetVocalRangeAsync(ScaleMode mode)
        => http.GetFromJsonAsync(Url($"api/modal-melodies/voice?mode={mode}"), Json.VocalRangeDto);

    /// <summary>A fresh open guitar chord jam; the same seed and settings always bring back the same one.</summary>
    public Task<ChordJamDto?> GetChordJamAsync(int seed, ChordJamSettings settings)
    {
        (string Name, double Value)[] query =
        [
            ("seed", seed), ("open", settings.OpenChords), ("variety", settings.Variety), ("mood", settings.Mood),
            ("keyChanges", settings.KeyChanges), ("difficulty", settings.Difficulty), ("colour", settings.Colour),
            ("speed", settings.ChangeSpeed), ("busyness", settings.Busyness), ("tempo", settings.Tempo),
            ("borrowed", settings.Borrowed), ("smoothness", settings.Smoothness), ("length", settings.Length),
            ("position", settings.Position), ("capo", settings.Capo), ("strumVariety", settings.StrumVariety),
            ("slidChords", settings.SlidChords), ("bass", settings.Bass),
        ];
        // Invariant, so a comma-decimal locale still sends 0.5 rather than 0,5.
        var values = query.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Name}={pair.Value}"));
        return http.GetFromJsonAsync(Url($"api/chord-jams/random?{string.Join('&', values)}"), Json.ChordJamDto);
    }

    /// <summary>A take sung over a guitar jam song, with the song itself (so the server lays out the
    /// chords that actually played) and the tempo it was played at. The reply says why when refused.</summary>
    public async Task<(JobStatusDto? Job, string? Error)> UploadJamTakeAsync(byte[] wavBytes, ChordJamDto song, double bpm)
    {
        using var content = WavForm(wavBytes, "jam.wav");
        content.Add(new StringContent(JsonSerializer.Serialize(song, Json.ChordJamDto)), "song");
        using var response = await http.PostAsync(Url(string.Create(CultureInfo.InvariantCulture, $"api/chord-jams/take?bpm={bpm}")), content);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(Json.JobStatusDto), null)
            : (null, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The caller's earlier takes over one guitar jam song, by its fingerprint.</summary>
    public Task<TakeHistoryDto?> GetJamTakesAsync(ChordJamDto song)
        => http.GetFromJsonAsync(Url($"api/chord-jams/takes?song={JamSongs.Fingerprint(song)}&title={Uri.EscapeDataString(song.Title ?? "")}"),
            Json.TakeHistoryDto);

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// The server's refusals are written for people: a ProblemDetails <c>detail</c> (or <c>title</c>),
    /// or a JSON string. Anything else gets a plain sentence rather than a status code.
    /// </summary>
    public static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { Length: > 0 } text)
            {
                return text;
            }
            if (root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("detail", out var detail) || root.TryGetProperty("title", out detail))
                && detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: > 0 } said)
            {
                return said;
            }
        }
        catch (JsonException)
        {
            // An empty or non-JSON body: fall through to the generic sentence.
        }
        return response.StatusCode == HttpStatusCode.NotFound
            ? "The upload expired before the analysis could start. Please upload the file again."
            : "The analyzer would not accept the file.";
    }

    private static MultipartFormDataContent WavForm(byte[] wavBytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", fileName);
        return content;
    }

    /// <summary>The body on any 2xx, null otherwise (a reading that does not exist yet, or ever).</summary>
    private async Task<T?> SuccessOrNullAsync<T>(string relative, JsonTypeInfo<T> type)
    {
        using var response = await http.GetAsync(Url(relative));
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(type) : default;
    }

    /// <summary>The body on 200 only: these routes answer 204 for "nothing honest to say".</summary>
    private async Task<T?> OkOrNullAsync<T>(string relative, JsonTypeInfo<T> type)
    {
        using var response = await http.GetAsync(Url(relative));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(type) : default;
    }
}
