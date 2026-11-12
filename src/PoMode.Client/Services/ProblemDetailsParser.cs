using System.Net.Http.Json;
using System.Text.Json;
using PoMode.Shared.Serialization;
using PoMode.Shared.Validation;

namespace PoMode.Client.Services;

/// <summary>
/// Shared RFC 7807 ProblemDetails response parser (Rule 14).
/// </summary>
internal static class ProblemDetailsParser
{
    public static async Task<ProblemDetailsDto?> TryReadAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(PoModeJsonContext.Default.ProblemDetailsDto, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
