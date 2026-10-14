using FlightLog.Domain.Common;
using FlightLog.Domain.Teams;

namespace FlightLog.UnitTests.Domain;

public sealed class TeamTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Creator = Guid.NewGuid();

    [Fact]
    public void Create_makes_the_creator_the_only_project_manager()
    {
        var team = Team.Create("  Flight Test  ", Creator, Now);

        team.Name.Should().Be("Flight Test");
        team.CreatedAt.Should().Be(Now);
        team.Members.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { UserId = Creator, Role = TeamRole.ProjectManager, TeamId = team.Id });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_blank_names(string? name)
    {
        var act = () => Team.Create(name!, Creator, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("name.required");
    }

    [Fact]
    public void Create_rejects_too_long_names()
    {
        var act = () => Team.Create(new string('x', Team.NameMaxLength + 1), Creator, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("name.too_long");
    }

    [Fact]
    public void AddMember_rejects_duplicates()
    {
        var team = Team.Create("Team", Creator, Now);
        var user = Guid.NewGuid();
        team.AddMember(user, TeamRole.Engineer, Now);

        var act = () => team.AddMember(user, TeamRole.Viewer, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("team.member_exists");
    }

    [Fact]
    public void AddMember_rejects_undefined_roles()
    {
        var team = Team.Create("Team", Creator, Now);

        var act = () => team.AddMember(Guid.NewGuid(), (TeamRole)42, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("team.invalid_role");
    }

    [Fact]
    public void The_last_project_manager_cannot_be_demoted_or_removed()
    {
        var team = Team.Create("Team", Creator, Now);
        team.AddMember(Guid.NewGuid(), TeamRole.Engineer, Now);

        var demote = () => team.ChangeRole(Creator, TeamRole.Viewer);
        var remove = () => team.RemoveMember(Creator);

        demote.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("team.last_project_manager");
        remove.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("team.last_project_manager");
        team.RoleOf(Creator).Should().Be(TeamRole.ProjectManager);
    }

    [Fact]
    public void A_project_manager_can_step_down_when_another_one_exists()
    {
        var team = Team.Create("Team", Creator, Now);
        var second = Guid.NewGuid();
        team.AddMember(second, TeamRole.ProjectManager, Now);

        team.ChangeRole(Creator, TeamRole.Engineer);

        team.RoleOf(Creator).Should().Be(TeamRole.Engineer);
        team.RoleOf(second).Should().Be(TeamRole.ProjectManager);

        // The second manager is now the last one and therefore cannot leave.
        var removeLast = () => team.RemoveMember(second);
        removeLast.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("team.last_project_manager");
    }

    [Fact]
    public void RemoveMember_of_unknown_user_fails()
    {
        var team = Team.Create("Team", Creator, Now);

        var act = () => team.RemoveMember(Guid.NewGuid());

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("team.member_not_found");
    }

    [Theory]
    [InlineData(TeamRole.ProjectManager, TeamRole.Engineer, true)]
    [InlineData(TeamRole.ProjectManager, TeamRole.ProjectManager, true)]
    [InlineData(TeamRole.Engineer, TeamRole.Viewer, true)]
    [InlineData(TeamRole.Engineer, TeamRole.ProjectManager, false)]
    [InlineData(TeamRole.Viewer, TeamRole.Engineer, false)]
    public void Roles_are_ordered(TeamRole role, TeamRole required, bool expected) =>
        role.Includes(required).Should().Be(expected);
}
