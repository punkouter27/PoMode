using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using PoMode.Shared.Serialization;

namespace PoMode.API.Features.ChordJams;

/// <summary><c>GET /api/chord-jams/random</c>: Practice's Guitar jam tab, a song to play.
/// <c>POST /api/chord-jams/take</c>: a take sung over one, queued for analysis against its chords.</summary>
public static class ChordJamEndpoints
{
    public static IEndpointRouteBuilder MapChordJams(this IEndpointRouteBuilder app)
    {
        // Made fresh each time. The seed and the sliders name a jam, so the same ones always give the same
        // song back; without a seed the server picks, and a slider left out is at its default.
        app.MapGet("/api/chord-jams/random", (int? seed, [AsParameters] JamQuery query) =>
            TypedResults.Ok(OpenChordJams.Generate(seed ?? Random.Shared.Next(), query.Settings())));

        // A take sung over a jam song: the recording as a multipart file, the song it was sung over as a
        // JSON form field (whole, so a saved song is analysed against the chords it actually played),
        // and the tempo it was played at. The job is an ordinary library take whose harmony is the song's.
        app.MapPost("/api/chord-jams/take", async Task<Results<Ok<JobStatusDto>, BadRequest<string>>> (
            HttpRequest request, double bpm, JamTakeSeeder seeder, AnalysisIntake intake, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return TypedResults.BadRequest("Expected a multipart form upload.");
            }
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (InvalidDataException)
            {
                return TypedResults.BadRequest("No recording uploaded.");
            }
            if (form.Files.FirstOrDefault() is not { } file)
            {
                return TypedResults.BadRequest("No recording uploaded.");
            }
            if (await AudioFormatValidator.ValidateAsync(file, ct) is { } rejection)
            {
                return TypedResults.BadRequest(rejection == UploadRejection.TooLarge
                    ? "The recording exceeds the 100 MB limit."
                    : "The recording must be .wav or .mp3 audio.");
            }
            ChordJamDto? song;
            try
            {
                song = JsonSerializer.Deserialize(form["song"].ToString(), PoModeJsonContext.Default.ChordJamDto);
            }
            catch (JsonException)
            {
                song = null;
            }
            if (song is null)
            {
                return TypedResults.BadRequest("The song the take was sung over could not be read.");
            }
            if (JamTakeSeeder.Problem(song, bpm) is { } problem)
            {
                return TypedResults.BadRequest(problem);
            }

            await using var stream = file.OpenReadStream();
            var state = await intake.StartAsync(
                fileName: $"Jam_{song.Key.Replace(' ', '_')}.wav",
                content: stream,
                clientCanInfer: false,
                ct: ct,
                seed: (job, token) => seeder.SeedAsync(job, song, bpm, token),
                ownerId: PoUser.IdOf(request.HttpContext.User));
            return TypedResults.Ok(state.ToDto());
        })
        .RequireAuthorization()
        .DisableAntiforgery()
        .WithName("AnalyzeJamTake")
        .WithSummary("Stores a take sung over a guitar jam song and queues it for analysis against that song's chords.");

        // The caller's takes over one song (its JamSongs fingerprint), with the modes their melodies came
        // out in: a mirror, like the Sing tab's history, not a grade.
        app.MapGet("/api/chord-jams/takes", async Task<Results<Ok<TakeHistoryDto>, BadRequest<string>>> (
            string song, string? title, HttpContext context, HumTakeHistory history, CancellationToken ct) =>
            song.Length == 16 && song.All(char.IsAsciiHexDigitLower)
                ? TypedResults.Ok(await history.ForJamSongAsync(PoUser.IdOf(context.User), song, title?[..Math.Min(title.Length, 80)], ct))
                : TypedResults.BadRequest("Unknown song."))
        .RequireAuthorization()
        .WithName("GetJamTakeHistory")
        .WithSummary("The caller's earlier takes over one guitar jam song, with the mode the analyzer found for each.");
        return app;
    }
}

/// <summary>The sliders as query values (<c>?open=1&amp;mood=0.2&amp;capo=3</c>), each optional.</summary>
public sealed class JamQuery
{
    public double? Open { get; init; }
    public double? Variety { get; init; }
    public double? Mood { get; init; }
    public int? KeyChanges { get; init; }
    public double? Difficulty { get; init; }
    public double? Colour { get; init; }
    public int? Speed { get; init; }
    public double? Busyness { get; init; }
    public double? Tempo { get; init; }
    public double? Borrowed { get; init; }
    public double? Smoothness { get; init; }
    public double? Length { get; init; }
    public double? Position { get; init; }
    public int? Capo { get; init; }
    public double? StrumVariety { get; init; }
    public double? SlidChords { get; init; }

    public ChordJamSettings Settings()
    {
        var d = new ChordJamSettings();
        return new(Open ?? d.OpenChords, Variety ?? d.Variety, Mood ?? d.Mood, KeyChanges ?? d.KeyChanges,
            Difficulty ?? d.Difficulty, Colour ?? d.Colour, Speed ?? d.ChangeSpeed, Busyness ?? d.Busyness,
            Tempo ?? d.Tempo, Borrowed ?? d.Borrowed, Smoothness ?? d.Smoothness, Length ?? d.Length,
            Position ?? d.Position, Capo ?? d.Capo, StrumVariety ?? d.StrumVariety, SlidChords ?? d.SlidChords);
    }
}
