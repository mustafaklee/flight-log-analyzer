using FlightLog.Domain.Common;
using FlightLog.Domain.Projects;

namespace FlightLog.UnitTests.Domain;

public sealed class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_values_and_treats_blank_description_as_unknown()
    {
        var teamId = Guid.NewGuid();

        var project = Project.Create(teamId, "  VTOL campaign ", "   ", Now);

        project.TeamId.Should().Be(teamId);
        project.Name.Should().Be("VTOL campaign");
        project.Description.Should().BeNull();
        project.CreatedAt.Should().Be(Now);
        project.UpdatedAt.Should().Be(Now);
        project.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_requires_a_team()
    {
        var act = () => Project.Create(Guid.Empty, "Name", null, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("teamId.required");
    }

    [Fact]
    public void Update_changes_values_and_timestamp()
    {
        var project = Project.Create(Guid.NewGuid(), "Old", null, Now);
        var later = Now.AddHours(1);

        project.Update("New", "Endurance tests", later);

        project.Name.Should().Be("New");
        project.Description.Should().Be("Endurance tests");
        project.UpdatedAt.Should().Be(later);
        project.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void Update_rejects_too_long_description()
    {
        var project = Project.Create(Guid.NewGuid(), "Name", null, Now);

        var act = () => project.Update("Name", new string('d', Project.DescriptionMaxLength + 1), Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("description.too_long");
    }
}
