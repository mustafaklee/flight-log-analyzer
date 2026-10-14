using FlightLog.Domain.Common;
using FlightLog.Domain.Fleet;

namespace FlightLog.UnitTests.Domain;

public sealed class AircraftTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static AircraftDetails Details(IReadOnlyCollection<string>? tags = null, AircraftType type = AircraftType.Multicopter) =>
        new("  Quad-01 ", "  SN-1  ", type, Autopilot.Px4, null, " ", null, null, null, tags);

    [Fact]
    public void Unknown_details_stay_null()
    {
        var aircraft = Aircraft.Create(Guid.NewGuid(), Details(), Now);

        aircraft.Name.Should().Be("Quad-01");
        aircraft.Identifier.Should().Be("SN-1");
        aircraft.FirmwareName.Should().BeNull();
        aircraft.FirmwareVersion.Should().BeNull();
        aircraft.Hardware.Should().BeNull();
        aircraft.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Undefined_type_is_rejected()
    {
        var act = () => Aircraft.Create(Guid.NewGuid(), Details(type: (AircraftType)99), Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("aircraft.invalid_type");
    }

    [Fact]
    public void Tags_are_trimmed_and_deduplicated_case_insensitively()
    {
        var aircraft = Aircraft.Create(Guid.NewGuid(), Details([" test ", "TEST", "", "night"]), Now);

        aircraft.Tags.Should().Equal(["test", "night"]);
    }

    [Fact]
    public void Tag_limits_are_enforced()
    {
        var tooMany = () => Tags.Normalize(Enumerable.Range(0, Tags.MaxCount + 1).Select(i => $"t{i}"));
        var tooLong = () => Tags.Normalize([new string('x', Tags.MaxLength + 1)]);

        tooMany.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("tags.too_many");
        tooLong.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("tags.too_long");
    }
}
