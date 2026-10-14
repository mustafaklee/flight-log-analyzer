using FlightLog.Domain.Projects;
using FluentValidation;

namespace FlightLog.Application.Projects;

internal sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Project.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Project.DescriptionMaxLength);
    }
}

internal sealed class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Project.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Project.DescriptionMaxLength);
    }
}
