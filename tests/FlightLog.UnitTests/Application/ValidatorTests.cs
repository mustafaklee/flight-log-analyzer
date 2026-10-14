using FlightLog.Application.Projects;
using FlightLog.Application.Teams;
using FlightLog.Domain.Projects;
using FlightLog.Domain.Teams;

namespace FlightLog.UnitTests.Application;

public sealed class ValidatorTests
{
    [Fact]
    public void CreateTeam_requires_a_name()
    {
        var result = new CreateTeamRequestValidator().Validate(new CreateTeamRequest(""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(CreateTeamRequest.Name));
    }

    [Theory]
    [InlineData("not-an-email", TeamRole.Engineer)]
    [InlineData("user@example.com", (TeamRole)99)]
    public void AddMember_rejects_invalid_input(string email, TeamRole role) =>
        new AddTeamMemberRequestValidator().Validate(new AddTeamMemberRequest(email, role)).IsValid.Should().BeFalse();

    [Fact]
    public void AddMember_accepts_valid_input() =>
        new AddTeamMemberRequestValidator().Validate(new AddTeamMemberRequest("user@example.com", TeamRole.Viewer)).IsValid.Should().BeTrue();

    [Fact]
    public void CreateProject_limits_lengths()
    {
        var request = new CreateProjectRequest(new string('n', Project.NameMaxLength + 1), new string('d', Project.DescriptionMaxLength + 1));

        var result = new CreateProjectRequestValidator().Validate(request);

        result.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(
            [nameof(CreateProjectRequest.Name), nameof(CreateProjectRequest.Description)]);
    }
}
