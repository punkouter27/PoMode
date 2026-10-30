using System.Text.Json;
using System.Text.Json.Serialization;
using PoMode.API.Features.Lyrics;
using PoMode.API.Features.SongIdentity;
using PoMode.API.Features.SongLoop;
using PoMode.Shared.Analysis;

namespace PoMode.API.Infrastructure;

/// <summary>
/// The shapes of the outside services the server reads (AcoustID, LRCLIB, Hooktheory), generated at
/// build time like <c>PoModeJsonContext</c>, so no request to them serializes by reflection. Web
/// defaults: camelCase out, case-insensitive in, which is what each service speaks.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AcoustIdClient.LookupResponse), TypeInfoPropertyName = "AcoustIdLookup")]
[JsonSerializable(typeof(LrclibClient.Record), TypeInfoPropertyName = "LrclibRecord")]
[JsonSerializable(typeof(List<ProgressionSongDto>), TypeInfoPropertyName = "HooktheorySongs")]
[JsonSerializable(typeof(HooktheoryClient.SignIn), TypeInfoPropertyName = "HooktheorySignIn")]
[JsonSerializable(typeof(HooktheoryClient.SignedIn), TypeInfoPropertyName = "HooktheorySignedIn")]
internal sealed partial class ExternalJsonContext : JsonSerializerContext;
