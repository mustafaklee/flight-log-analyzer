using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;

namespace FlightLog.UnitTests.Domain;

public sealed class FlightTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static Flight NewFlight() =>
        Flight.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, " Hover test ", Autopilot.Px4, Guid.NewGuid(), Now);

    [Fact]
    public void Log_derived_values_are_unknown_until_recorded()
    {
        var flight = NewFlight();

        flight.Name.Should().Be("Hover test");
        flight.AircraftId.Should().BeNull();
        flight.StartedAt.Should().BeNull();
        flight.DurationUs.Should().BeNull();
        flight.FirmwareVersion.Should().BeNull();
        flight.OccurredAt.Should().Be(Now);
    }

    [Fact]
    public void Recorded_start_time_becomes_the_listing_date()
    {
        var flight = NewFlight();
        var started = Now.AddDays(-3);

        flight.RecordLogDetails(
            new FlightLogDetails
            {
                StartedAt = started,
                DurationUs = 90_000_000,
                FirmwareVersion = " v1.15.0 ",
                VehicleType = "Copter",
                LogStartTimestampUs = 1_000,
                Metadata = new Dictionary<string, string> { ["frame"] = "QUAD" },
            },
            Now.AddMinutes(5));

        flight.OccurredAt.Should().Be(started);
        flight.DurationUs.Should().Be(90_000_000);
        flight.FirmwareVersion.Should().Be("v1.15.0");
        flight.VehicleType.Should().Be("Copter");
        flight.LogStartTimestampUs.Should().Be(1_000);
        flight.LogMetadata.Should().Contain("frame", "QUAD");
        flight.UpdatedAt.Should().Be(Now.AddMinutes(5));

        flight.RecordLogDetails(new FlightLogDetails(), Now.AddMinutes(6));
        flight.OccurredAt.Should().Be(Now, "an unknown start time falls back to the upload time");
        flight.FirmwareVersion.Should().BeNull();
        flight.LogMetadata.Should().BeEmpty();
    }

    [Fact]
    public void Negative_duration_is_rejected()
    {
        var flight = NewFlight();

        var act = () => flight.RecordLogDetails(new FlightLogDetails { DurationUs = -1 }, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("flight.negative_duration");
    }

    [Fact]
    public void Overlong_values_from_the_log_are_shortened()
    {
        var flight = NewFlight();

        flight.RecordLogDetails(new FlightLogDetails { FirmwareVersion = new string('v', 300), Hardware = new string('h', 300) }, Now);

        flight.FirmwareVersion.Should().HaveLength(200);
        flight.Hardware.Should().HaveLength(200);
    }
}
