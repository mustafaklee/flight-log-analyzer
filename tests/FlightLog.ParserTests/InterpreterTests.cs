using FlightLog.LogParsing.DataFlash;
using FlightLog.LogParsing.Interpretation;
using FlightLog.LogParsing.ULog;

namespace FlightLog.ParserTests;

/// <summary>Flight-level facts extracted from real logs. Boot times were cross-checked with pyulog and pymavlink.</summary>
public sealed class InterpreterTests
{
    private static FlightSummary Px4(string file)
    {
        var interpreter = new Px4Interpreter();
        using var stream = Fixtures.Open(Path.Combine("px4", file));
        var report = new ULogParser().Parse(stream, interpreter, new ParserLimits(), TestContext.Current.CancellationToken);
        return interpreter.Complete(report);
    }

    private static FlightSummary ArduPilot(string file)
    {
        var interpreter = new ArduPilotInterpreter();
        using var stream = Fixtures.Open(Path.Combine("ardupilot", file));
        var report = new DataFlashParser().Parse(stream, interpreter, new ParserLimits(), TestContext.Current.CancellationToken);
        return interpreter.Complete(report);
    }

    private static IEnumerable<string> Texts(FlightSummary summary, FlightEventKind kind) =>
        summary.Events.Where(e => e.Kind == kind).Select(e => e.Text);

    [Fact]
    public void Px4_flight_with_gps_gets_firmware_vehicle_arming_and_utc_start()
    {
        var summary = Px4("sample_log_small.ulg");

        summary.FirmwareVersion.Should().Be("PX4 v1.11.2 (dev)");
        summary.Hardware.Should().Be("CUBEPILOT_CUBEORANGE");
        summary.VehicleType.Should().Be("Vtol");
        summary.Metadata.Should().Contain("sys_os_name", "NuttX");

        summary.BootTimeUtc.Should().Be(new DateTimeOffset(2021, 4, 21, 6, 30, 38, TimeSpan.Zero).AddTicks(1286970));
        summary.FirstTimestampUs.Should().Be(20_309_082, "the logger start time from the header, as pyulog reports");
        summary.StartedAtUtc.Should().Be(summary.BootTimeUtc!.Value.AddTicks(20_309_082 * 10));

        summary.Events.Should().ContainSingle(e => e.Kind == FlightEventKind.Armed).Which.TimestampUs.Should().Be(20_220_673);
        summary.Events.Should().ContainSingle(e => e.Kind == FlightEventKind.Disarmed).Which.TimestampUs.Should().Be(25_829_736);
        Texts(summary, FlightEventKind.Message).Should().Contain(["[commander] Takeoff detected", "[commander] Landing detected"]);
        summary.Events.Select(e => e.TimestampUs).Should().BeInAscendingOrder();
        summary.Parameters.Should().HaveCount(980).And.OnlyContain(p => p.TimestampUs == null);
    }

    [Fact]
    public void Px4_mode_sequence_follows_vehicle_status()
    {
        var summary = Px4("sample_px4_events.ulg");

        Texts(summary, FlightEventKind.ModeChange).Should().Equal(
            "Mode AUTO_LOITER", "Mode AUTO_TAKEOFF", "Mode AUTO_LOITER", "Mode AUTO_RTL", "Mode AUTO_LOITER");
        summary.Events.Single(e => e.Kind == FlightEventKind.Armed).TimestampUs.Should().Be(1_710_773_365_282_000);
        summary.VehicleType.Should().Be("RotaryWing");
        summary.FirmwareVersion.Should().Be("PX4 v1.15.0 (dev)");

        // SITL timestamps count from the Unix epoch; GPS time still gives the right wall clock (log file 14_49_10.ulg).
        summary.StartedAtUtc.Should().Be(new DateTimeOffset(2024, 3, 18, 14, 49, 10, 346, TimeSpan.Zero));
    }

    [Fact]
    public void Px4_log_without_release_tag_or_gps_keeps_unknowns_null()
    {
        var summary = Px4("sample.ulg");

        summary.FirmwareVersion.Should().Be("PX4 git fd483321");
        summary.Hardware.Should().Be("AUAV_X21");
        summary.VehicleType.Should().BeNull("this old log has no vehicle_type field");
        summary.BootTimeUtc.Should().BeNull("the GPS never reported a UTC time");
        summary.StartedAtUtc.Should().BeNull();
        summary.DurationUs.Should().Be(181_493_506 - 112_500_176);
        summary.Events.Count(e => e.Kind == FlightEventKind.Dropout).Should().Be(4);
        summary.Events.Where(e => e.Kind == FlightEventKind.Message).Should().OnlyContain(e => e.Severity == LogSeverity.Error);
    }

    [Fact]
    public void Px4_default_parameters_are_attached_to_the_initial_values()
    {
        var summary = Px4("sample_logging_tagged_and_default_params.ulg");

        summary.Parameters.Should().NotBeEmpty().And.OnlyContain(p => !double.IsNaN(p.Value), "defaults never replace a value");
        summary.Parameters.Should().Contain(p => p.DefaultValue != null);
        Texts(summary, FlightEventKind.Message).Should().Contain("tagged message test");
    }

    [Fact]
    public void ArduPilot_flight_gets_firmware_modes_events_parameters_and_utc_start()
    {
        var expected = Fixtures.Expected<ExpectedDataFlash>("sitl-copter-4.6.3.bin");

        var summary = ArduPilot("sitl-copter-4.6.3.bin");

        summary.FirmwareVersion.Should().Be("ArduCopter V4.6.3 (92b0cd78)");
        summary.VehicleType.Should().Be("Copter");
        summary.Metadata.Should().Contain("frame", "QUAD/PLUS").And.Contain("git_hash", "92b0cd78");

        Texts(summary, FlightEventKind.ModeChange).Should().Equal("Mode GUIDED", "Mode GUIDED", "Mode LOITER", "Mode RTL");
        summary.Events.Should().ContainSingle(e => e.Kind == FlightEventKind.Disarmed).Which.TimestampUs.Should().Be(90_949_439);
        Texts(summary, FlightEventKind.Event).Should().Contain(["AUTO_ARMED", "NOT_LANDED", "LAND_COMPLETE", "EKF_YAW_RESET"])
            .And.NotContain(t => t.StartsWith("Event ", StringComparison.Ordinal), "every logged event id has a name");
        Texts(summary, FlightEventKind.Message).Should().Contain("ArduCopter V4.6.3 (92b0cd78)");

        summary.Parameters.Where(p => p.TimestampUs == null).Select(p => p.Name).Should().BeEquivalentTo(expected.Parameters.Keys);
        summary.Parameters.Where(p => p.TimestampUs != null).Should().OnlyContain(p => p.TimestampUs > 0);

        // GPS week/ms of the first 3D fix minus TimeUS, minus 18 leap seconds (pymavlink gives the same).
        summary.BootTimeUtc.Should().Be(new DateTimeOffset(2026, 10, 14, 23, 30, 35, TimeSpan.Zero).AddTicks(7906810));
        summary.FirstTimestampUs.Should().Be(24_509_359);
    }
}
