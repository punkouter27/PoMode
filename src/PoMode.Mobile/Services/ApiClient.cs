using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;

namespace PoMode.Mobile.Services;

/// <summary>
/// Every call the web client's AnalysisClient makes, against the same routes, through the shared source-generated
/// <see cref="PoModeJsonContext"/> (the phone and the server can't disagree on a field).
/// </summary>
public sealed class ApiClient(HttpClient http, MobileConfig config, CookieJar cookies)
{
    private static PoModeJsonContext Json => PoModeJsonContext.Default;

    public Uri Url(string relative) => new(new Uri(config.BaseUrl + "/"), relative.TrimStart('/'));

    // ---- Session -------------------------------------------------------------------------------------------

    public Task<SessionDto?> GetSessionAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync(Url("api/auth/session"), Json.SessionDto, ct);

    /// <summary>A persistent guest (any environment): the PoMode.Session cookie is the account, kept in SecureStorage.</summary>
    public async Task<SessionDto?> StartGuestAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync(Url("auth/guest"), null, ct);
        response.EnsureSuccessStatusCode();
        await cookies.SaveAsync();
        return await response.Content.ReadFromJsonAsync(Json.SessionDto, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync(Url("auth/logout"), null, ct);
        cookies.Clear();
    }

    public Task<DiagReportDto?> GetDiagnosticsAsync(CancellationToken ct = default)
        => http.GetFromJsonAsync(Url("api/diag"), Json.DiagReportDto, ct);

    // ---- Library -------------------------------------------------------------------------------------------

    public async Task<List<LibraryEntryDto>> GetLibraryAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync(Url("api/library"), Json.ListLibraryEntryDto, ct) ?? [];

    public async Task<bool> DeleteSongAsync(string jobId)
    {
        using var response = await http.DeleteAsync(Url($"api/library/{jobId}"));
        return response.IsSuccessStatusCode;
    }

    public async Task<int> DeleteMyDataAsync()
    {
        using var response = await http.DeleteAsync(Url("api/account/data"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(Json.Int32);
    }

    public Task<List<SimilarSongDto>?> GetSimilarAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/library/{jobId}/similar"), Json.ListSimilarSongDto);

    /// <summary>Null when this server cannot search by sound (Azure, or the CLAP models missing).</summary>
    public async Task<List<SoundMatchDto>?> SearchBySoundAsync(string words)
    {
        using var response = await http.GetAsync(Url($"api/library/sound?q={Uri.EscapeDataString(words)}"));
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.ListSoundMatchDto) : null;
    }

    // ---- One analysis --------------------------------------------------------------------------------------

    public Task<JobStatusDto?> GetStatusAsync(string jobId, CancellationToken ct = default)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}"), Json.JobStatusDto, ct);

    public async Task CancelAsync(string jobId)
    {
        using var response = await http.DeleteAsync(Url($"api/analysis/{jobId}"));
    }

    public Task<ModalResult?> GetResultAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}/result"), Json.ModalResult);

    public async Task<string[]> GetStemsAsync(string jobId)
        => await http.GetFromJsonAsync(Url($"api/analysis/{jobId}/stems"), Json.StringArray) ?? [];

    public Task<AnalysisPreviewDto?> GetPreviewAsync(string jobId) => OptionalAsync($"api/analysis/{jobId}/preview", Json.AnalysisPreviewDto);

    public Task<VisualizationPayload?> GetVisualAsync(string jobId)
        => http.GetFromJsonAsync(Url($"api/analysis/{jobId}/visual"), Json.VisualizationPayload);

    public Task<VoiceProfileDto?> GetVoiceProfileAsync(string jobId) => OptionalAsync($"api/analysis/{jobId}/voice", Json.VoiceProfileDto);

    public Task<SongStats?> GetStatsAsync(string jobId) => OptionalAsync($"api/analysis/{jobId}/stats", Json.SongStats);

    public Task<ChordShapesDto?> GetChordShapesAsync(string jobId) => OptionalAsync($"api/analysis/{jobId}/shapes", Json.ChordShapesDto);

    public async Task<SongLoopDto?> GetSongLoopAsync(string jobId)
    {
        using var response = await http.GetAsync(Url($"api/analysis/{jobId}/loop"));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(Json.SongLoopDto) : null;
    }

    public Task<List<ProgressionSongDto>?> GetSongsOnLoopAsync(string jobId) => OptionalAsync($"api/analysis/{jobId}/loop/songs", Json.ListProgressionSongDto);

    public async Task<List<LyricLineDto>?> GetLyricsAsync(string jobId)
    {
        using var response = await http.GetAsync(Url($"api/analysis/{jobId}/lyrics"));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(Json.ListLyricLineDto) : null;
    }

    public async Task<SongVoiceFitDto?> GetSongVoiceFitAsync(string jobId)
    {
        using var response = await http.GetAsync(Url($"api/modal-melodies/voice/song/{jobId}"));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(Json.SongVoiceFitDto) : null;
    }

    public async Task<double?> GetEtaAsync(string jobId)
        => (await OptionalAsync($"api/analysis/{jobId}/eta", Json.EtaDto))?.SecondsLeft;

    public Task<List<StageExecutorsDto>?> GetExecutorsAsync()
        => http.GetFromJsonAsync(Url("api/analysis/executors"), Json.ListStageExecutorsDto);

    public Task<List<InterpreterOptionDto>?> GetInterpretersAsync()
        => http.GetFromJsonAsync(Url("api/analysis/interpreters"), Json.ListInterpreterOptionDto);

    public async Task<(JobStatusDto? Status, string? Error)> RerunAsync(string jobId, string stage, string executor)
    {
        using var response = await http.PostAsync(
            Url($"api/analysis/{jobId}/rerun?stage={Uri.EscapeDataString(stage)}&executor={Uri.EscapeDataString(executor)}"), null);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(Json.JobStatusDto), null)
            : (null, await ReasonAsync(response));
    }

    /// <summary>A stem ("vocals", "instrumental", "mix") or export, saved under the cache folder for playback or sharing.</summary>
    public async Task<string?> DownloadAsync(string relative, string fileName, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Url(relative), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await using (FileStream file = File.Create(path))
        {
            await response.Content.CopyToAsync(file, ct);
        }

        return path;
    }

    // ---- Interpretation (server-sent events) ---------------------------------------------------------------

    public IAsyncEnumerable<InterpretationEvent> StreamInterpretationAsync(string jobId, string? interpreter, CancellationToken ct = default)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Get, Url($"api/analysis/{jobId}/interpretation"
            + (string.IsNullOrWhiteSpace(interpreter) ? "" : $"?interpreter={Uri.EscapeDataString(interpreter)}"))), ct);

    public IAsyncEnumerable<InterpretationEvent> StreamAnswerAsync(
        string jobId, string question, IReadOnlyList<InterpretationTurn> history, string? interpreter, CancellationToken ct = default)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Post, Url($"api/analysis/{jobId}/interpretation/ask"))
        {
            Content = JsonContent.Create(new SongQuestionRequest(question, history, interpreter), Json.SongQuestionRequest),
        }, ct);

    private async IAsyncEnumerable<InterpretationEvent> StreamAsync(HttpRequestMessage request, [EnumeratorCancellation] CancellationToken ct)
    {
        using HttpRequestMessage owned = request;
        owned.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(owned, HttpCompletionOption.ResponseHeadersRead, ct);
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

    // ---- Starting analyses ---------------------------------------------------------------------------------

    /// <summary>
    /// Starts analysing a finished resumable upload. The phone never claims clientCanInfer: it cannot run the browser's
    /// Tier-2 pitch tracker, so the server keeps every stage. A repeat for the same upload answers with the same job.
    /// </summary>
    public async Task<(JobStatusDto? Status, string? Error)> StartFromUploadAsync(
        string uploadId, IReadOnlyDictionary<string, string> executorChoices, CancellationToken ct = default)
    {
        List<string> query = [];
        foreach (var (stage, queryKey) in StageNames.ExecutorQueryKeys)
        {
            if (executorChoices.TryGetValue(stage, out var name))
            {
                query.Add($"{queryKey}={Uri.EscapeDataString(name)}");
            }
        }

        string url = $"api/analysis/uploads/{Uri.EscapeDataString(uploadId)}" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
        using var response = await http.PostAsync(Url(url), null, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(Json.JobStatusDto, ct), null)
            : (null, await ReasonAsync(response));
    }

    // ---- Mode Lab and Practice -----------------------------------------------------------------------------

    public Task<List<ChordProgressionDefinition>?> GetProgressionsAsync()
        => http.GetFromJsonAsync(Url("api/modal-melodies/progressions"), Json.ListChordProgressionDefinition);

    public async Task<GeneratedMelodyDto?> GenerateAsync(ModalMelodyRequest request)
    {
        using var response = await http.PostAsJsonAsync(Url("api/modal-melodies/generate"), request, Json.ModalMelodyRequest);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.GeneratedMelodyDto) : null;
    }

    public async Task<ModeLabDescriptionDto?> DescribeAsync(string text, ModalMelodyRequest current)
    {
        using var response = await http.PostAsJsonAsync(Url("api/modal-melodies/describe"), new ModeLabDescriptionRequest(text, current), Json.ModeLabDescriptionRequest);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.ModeLabDescriptionDto) : null;
    }

    public async Task<JobStatusDto?> AnalyzeMelodyAsync(ModalMelodyRequest request)
    {
        using var response = await http.PostAsJsonAsync(Url("api/modal-melodies/analyze"), request, Json.ModalMelodyRequest);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.JobStatusDto) : null;
    }

    /// <summary>The backing's query string, as the WAV and MIDI exports and the hum upload all read it.</summary>
    public static string BackingQuery(ModalMelodyRequest backing) =>
        string.Create(CultureInfo.InvariantCulture,
            $"tonicPitchClass={backing.TonicPitchClass}&mode={backing.Mode}&progressionId={Uri.EscapeDataString(backing.ProgressionId)}"
            + $"&bpm={backing.Bpm}&style={backing.Style}&seed={backing.Seed}&targetPurity={backing.TargetPurity}");

    /// <summary>The server-synthesized melody + chords (44.1 kHz WAV), saved for the player.</summary>
    public Task<string?> DownloadBackingWavAsync(ModalMelodyRequest backing, CancellationToken ct = default)
        => DownloadAsync($"api/modal-melodies/wav?{BackingQuery(backing)}", $"backing-{backing.Mode}-{backing.TonicPitchClass}-{backing.Seed}.wav", ct);

    public async Task<JobStatusDto?> UploadHumTakeAsync(byte[] wav, ModalMelodyRequest backing)
    {
        using MultipartFormDataContent content = [];
        ByteArrayContent file = new(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "hum.wav");
        using var response = await http.PostAsync(Url($"api/modal-melodies/hum?{BackingQuery(backing)}"), content);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.JobStatusDto) : null;
    }

    public Task<TakeHistoryDto?> GetTakeHistoryAsync(ModalMelodyRequest backing)
        => http.GetFromJsonAsync(Url(string.Create(CultureInfo.InvariantCulture,
            $"api/modal-melodies/takes?mode={backing.Mode}&progressionId={Uri.EscapeDataString(backing.ProgressionId)}&targetPurity={backing.TargetPurity}")), Json.TakeHistoryDto);

    public Task<VocalRangeDto?> GetVocalRangeAsync(ScaleMode mode)
        => http.GetFromJsonAsync(Url($"api/modal-melodies/voice?mode={mode}"), Json.VocalRangeDto);

    public Task<ChordJamDto?> GetChordJamAsync(int seed, ChordJamSettings s)
    {
        (string Name, double Value)[] query =
        [
            ("seed", seed), ("open", s.OpenChords), ("variety", s.Variety), ("mood", s.Mood), ("keyChanges", s.KeyChanges),
            ("difficulty", s.Difficulty), ("colour", s.Colour), ("speed", s.ChangeSpeed), ("busyness", s.Busyness), ("tempo", s.Tempo),
            ("borrowed", s.Borrowed), ("smoothness", s.Smoothness), ("length", s.Length), ("position", s.Position), ("capo", s.Capo),
            ("strumVariety", s.StrumVariety), ("slidChords", s.SlidChords), ("bass", s.Bass),
        ];
        var values = query.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Name}={pair.Value}"));
        return http.GetFromJsonAsync(Url($"api/chord-jams/random?{string.Join('&', values)}"), Json.ChordJamDto);
    }

    public async Task<(JobStatusDto? Job, string? Error)> UploadJamTakeAsync(byte[] wav, ChordJamDto song, double bpm)
    {
        using MultipartFormDataContent content = [];
        ByteArrayContent file = new(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "jam.wav");
        content.Add(new StringContent(JsonSerializer.Serialize(song, Json.ChordJamDto)), "song");
        using var response = await http.PostAsync(Url(string.Create(CultureInfo.InvariantCulture, $"api/chord-jams/take?bpm={bpm}")), content);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(Json.JobStatusDto), null)
            : (null, await response.Content.ReadAsStringAsync());
    }

    public Task<TakeHistoryDto?> GetJamTakesAsync(ChordJamDto song)
        => http.GetFromJsonAsync(Url($"api/chord-jams/takes?song={JamSongs.Fingerprint(song)}&title={Uri.EscapeDataString(song.Title ?? "")}"), Json.TakeHistoryDto);

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<T?> OptionalAsync<T>(string relative, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var response = await http.GetAsync(Url(relative));
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(type) : default;
    }

    /// <summary>The server's refusals are written for people: a ProblemDetails detail, or a JSON string.</summary>
    public static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.String && doc.RootElement.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && (doc.RootElement.TryGetProperty("detail", out var d) || doc.RootElement.TryGetProperty("title", out d))
                && d.GetString() is { Length: > 0 } detail)
            {
                return detail;
            }
        }
        catch (JsonException)
        {
        }

        return response.StatusCode == HttpStatusCode.NotFound
            ? "The upload expired before the analysis could start. Please upload the file again."
            : $"The server refused it ({(int)response.StatusCode}).";
    }
}
