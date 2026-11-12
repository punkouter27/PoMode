using FluentValidation;
using PoMode.Shared.Analysis;

namespace PoMode.Shared.Validation;

/// <summary>
/// FluentValidation rules for song follow-up questions (Rule 11 & Rule 17).
/// </summary>
public sealed class SongQuestionRequestValidator : AbstractValidator<SongQuestionRequest>
{
    public SongQuestionRequestValidator()
    {
        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Ask a question first.")
            .MaximumLength(QuestionPrompt.MaxQuestionLength)
            .WithMessage($"Questions must be {QuestionPrompt.MaxQuestionLength} characters or fewer.");
    }
}

/// <summary>
/// FluentValidation rules for Mode Lab loop description prompts.
/// </summary>
public sealed class ModeLabDescriptionRequestValidator : AbstractValidator<ModeLabDescriptionRequest>
{
    public ModeLabDescriptionRequestValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage("Describe how the loop should sound.")
            .MaximumLength(200).WithMessage("Descriptions must be 200 characters or fewer.");
    }
}

/// <summary>
/// FluentValidation rules for algorithmic modal melody requests.
/// </summary>
public sealed class ModalMelodyRequestValidator : AbstractValidator<ModalMelodyRequest>
{
    public ModalMelodyRequestValidator()
    {
        RuleFor(x => x.ProgressionId).NotEmpty().WithMessage("Choose a chord progression.");
        RuleFor(x => x.Bpm).InclusiveBetween(20.0, 320.0).WithMessage("Tempo must be between 20 and 320 BPM.");
        RuleFor(x => x.TargetPurity).InclusiveBetween(0.0, 100.0).WithMessage("Purity must be between 0 and 100%.");
    }
}

/// <summary>
/// Lightweight RFC 7807 ProblemDetails DTO compatible with System.Text.Json source generation.
/// </summary>
public sealed record ProblemDetailsDto(string? Title, string? Detail, int? Status);
