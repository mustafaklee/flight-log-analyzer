using FlightLog.Domain.Fleet;
using FluentValidation;

namespace FlightLog.Application.Fleet;

internal sealed class SaveAircraftRequestValidator : AbstractValidator<SaveAircraftRequest>
{
    public SaveAircraftRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Aircraft.NameMaxLength);
        RuleFor(x => x.Identifier).MaximumLength(Aircraft.IdentifierMaxLength);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Autopilot).IsInEnum();
        RuleFor(x => x.FirmwareName).MaximumLength(Aircraft.TextMaxLength);
        RuleFor(x => x.FirmwareVersion).MaximumLength(Aircraft.TextMaxLength);
        RuleFor(x => x.FlightController).MaximumLength(Aircraft.TextMaxLength);
        RuleFor(x => x.Hardware).MaximumLength(Aircraft.TextMaxLength);
        RuleFor(x => x.Description).MaximumLength(Aircraft.DescriptionMaxLength);
        RuleFor(x => x.Tags).Must(t => t is null || t.Count <= Domain.Common.Tags.MaxCount)
            .WithMessage($"At most {Domain.Common.Tags.MaxCount} tags are allowed.");
        RuleForEach(x => x.Tags).MaximumLength(Domain.Common.Tags.MaxLength);
    }
}
