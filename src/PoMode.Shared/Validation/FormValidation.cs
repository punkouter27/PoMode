using FluentValidation;

namespace PoMode.Shared.Validation;

/// <summary>
/// The rules behind the app's input forms, one copy for both sides: the page shows the message next
/// to the field before anything is sent, and the endpoint refuses the same input with the same words.
/// </summary>
public static class FormRules
{
    /// <summary>The upload cap, shared by the file pickers, the tus endpoint and the sniffing rule.</summary>
    public const long MaxAudioBytes = 100L * 1024 * 1024;

    public const int MaxDescriptionLength = 200;
    public const int MaxPresetNameLength = 40;
    public const int MaxFileNameLength = 255;

    /// <summary>A Mode Lab description: "darker and slower", "funky, in D".</summary>
    public static IRuleBuilderOptions<T, string> ModeLabDescription<T>(this IRuleBuilder<T, string> rule)
        => rule
            .Must(text => !string.IsNullOrWhiteSpace(text)).WithMessage("Describe how the loop should sound.")
            .MaximumLength(MaxDescriptionLength).WithMessage($"Descriptions must be {MaxDescriptionLength} characters or fewer.");
}

/// <summary>The Mode Lab's describe box, as the page edits it.</summary>
public sealed class ModeLabDescriptionForm
{
    public string Text { get; set; } = "";
}

public sealed class ModeLabDescriptionFormValidator : AbstractValidator<ModeLabDescriptionForm>
{
    public ModeLabDescriptionFormValidator() => RuleFor(x => x.Text).ModeLabDescription();
}

/// <summary>Three letters, A to Z, the way an arcade asks: the name a guest's library goes by.</summary>
public sealed class InitialsRequestValidator : AbstractValidator<PoMode.Shared.Account.InitialsRequest>
{
    public InitialsRequestValidator()
        => RuleFor(x => x.Initials)
            .Must(IsInitials).WithMessage("Type three letters, A to Z.");

    public static bool IsInitials(string? text)
        => text is { Length: 3 } && text.All(char.IsAsciiLetterUpper);
}

/// <summary>The practice page's "save these settings as" box. Presets live in this browser only, so
/// the page is the only place this rule runs.</summary>
public sealed class ChordJamPresetForm
{
    public string Name { get; set; } = "";
}

public sealed class ChordJamPresetFormValidator : AbstractValidator<ChordJamPresetForm>
{
    public ChordJamPresetFormValidator()
    {
        RuleFor(x => x.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Give these settings a name.")
            .Must(name => name.Trim().Length <= FormRules.MaxPresetNameLength)
            .WithMessage($"Names must be {FormRules.MaxPresetNameLength} characters or fewer.");
    }
}

/// <summary>A file picked for upload, before a byte of it is sent: its name and size.</summary>
public sealed record AudioFileChoice(string FileName, long Length);

/// <summary>
/// What a picked song or beat must be before it is uploaded. The bytes themselves are judged on the
/// server (AudioFormatValidator sniffs them); this is the part both sides can check from the name and
/// size alone, which the tus endpoint runs on every upload it creates.
/// </summary>
public sealed class AudioFileChoiceValidator : AbstractValidator<AudioFileChoice>
{
    public AudioFileChoiceValidator()
    {
        RuleFor(x => x.FileName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("The file has no name.")
            .MaximumLength(FormRules.MaxFileNameLength).WithMessage("The file name is too long.");
        RuleFor(x => x.Length)
            .GreaterThan(0).WithMessage("The file is empty.")
            .LessThanOrEqualTo(FormRules.MaxAudioBytes).WithMessage("File exceeds the 100 MB limit.");
    }
}
