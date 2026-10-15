using FlightLog.Domain.Flights;
using FluentValidation;

namespace FlightLog.Application.Flights;

internal sealed class UpdateFlightRequestValidator : AbstractValidator<UpdateFlightRequest>
{
    public UpdateFlightRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Flight.NameMaxLength);
        RuleFor(x => x.Notes).MaximumLength(Flight.NotesMaxLength);
    }
}

internal sealed class FlightQueryValidator : AbstractValidator<FlightQuery>
{
    public FlightQueryValidator()
    {
        RuleFor(x => x.Autopilot).IsInEnum().When(x => x.Autopilot is not null);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
        RuleFor(x => x.Sort).IsInEnum();
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Firmware).MaximumLength(200);
        RuleFor(x => x.Tag).MaximumLength(Domain.Common.Tags.MaxLength);
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From).When(x => x.From is not null && x.To is not null)
            .WithMessage("'to' must not be earlier than 'from'.");
    }
}

internal sealed class SetFlightTagsRequestValidator : AbstractValidator<SetFlightTagsRequest>
{
    public SetFlightTagsRequestValidator()
    {
        RuleFor(x => x.Tags).NotNull();
        RuleForEach(x => x.Tags).MaximumLength(Domain.Common.Tags.MaxLength);
        RuleFor(x => x.Tags).Must(t => t is null || t.Count <= Domain.Common.Tags.MaxCount * 2)
            .WithMessage($"At most {Domain.Common.Tags.MaxCount} tags are allowed.");
    }
}
