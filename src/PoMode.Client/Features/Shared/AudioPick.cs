using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoMode.Shared.Validation;

namespace PoMode.Client.Features.Shared;

/// <summary>
/// The song and beat pickers' check before upload: the picked file's name and size through
/// <see cref="AudioFileChoiceValidator"/> (PoMode.Shared, the rule the tus endpoint runs too).
/// </summary>
public static class AudioPick
{
    /// <summary>Null when the picked file may be uploaded, else the message to show under the picker
    /// (the input is cleared then). <paramref name="cancelled"/> is true when nothing was picked.</summary>
    public static async Task<(bool Cancelled, string? Error)> CheckAsync(
        IJSObjectReference uploader, ElementReference input, IValidator<AudioFileChoice> validator)
    {
        if (await uploader.InvokeAsync<AudioFileChoice?>("peek", input) is not { } choice)
        {
            return (true, null);
        }
        var result = await validator.ValidateAsync(choice);
        if (result.IsValid)
        {
            return (false, null);
        }
        await uploader.InvokeVoidAsync("clear", input);
        return (false, result.Errors[0].ErrorMessage);
    }
}
