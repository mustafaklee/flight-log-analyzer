using FlightLog.Domain.Teams;
using FluentValidation;

namespace FlightLog.Application.Teams;

internal sealed class CreateTeamRequestValidator : AbstractValidator<CreateTeamRequest>
{
    public CreateTeamRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Team.NameMaxLength);
    }
}

internal sealed class AddTeamMemberRequestValidator : AbstractValidator<AddTeamMemberRequest>
{
    public AddTeamMemberRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Role).IsInEnum();
    }
}

internal sealed class ChangeTeamMemberRoleRequestValidator : AbstractValidator<ChangeTeamMemberRoleRequest>
{
    public ChangeTeamMemberRoleRequestValidator()
    {
        RuleFor(x => x.Role).IsInEnum();
    }
}
