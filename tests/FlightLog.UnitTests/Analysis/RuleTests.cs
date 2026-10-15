using FlightLog.Application.Analysis;
using FlightLog.Application.Analysis.Rules;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;
using static FlightLog.UnitTests.Analysis.RuleTestContext;

namespace FlightLog.UnitTests.Analysis;

public sealed class RuleTests
{
    [Fact]
    public void Armed_periods_come_from_arm_events_and_a_leading_disarm_means_armed_from_log_start()
    {
        var context = new RuleTestContext().Armed(10, 20).Event(30, FlightEventKind.Armed, "Armed", "ARM").Build();
        context.ArmedIntervals.Should().Equal(new TimeRange(S(10), S(20)), new TimeRange(S(30), S(100)));
        context.WindowLimitation.Should().BeNull();

        var startedArmed = new RuleTestContext().Event(40, FlightEventKind.Disarmed, "Disarmed", "ARM").Build();
        startedArmed.Windows.Should().Equal(new TimeRange(0, S(40)));

        var unknown = new RuleTestContext().Build();
        unknown.Windows.Should().Equal(new TimeRange(0, S(100)));
        unknown.WindowLimitation.Should().NotBeNull();
    }

    [Fact]
    public async Task Gps_fix_loss_reports_losses_after_the_first_3d_fix_while_armed()
    {
        // No fix until 5 s (normal before lock), 3D fix, lost from 40 to 52 s, lost again at 90 s after disarm.
        var result = await new RuleTestContext()
            .Armed(2, 80)
            .Signal("gps.fix_type", t => t < 5 || (t >= 40 && t < 52) || t > 85 ? 1 : 3, periodS: 0.2)
            .RunAsync(new GpsFixLossRule());

        result.Status.Should().Be(RuleResultStatus.Applied);
        var finding = result.Findings.Should().ContainSingle().Which;
        finding.StartUs.Should().Be(S(40));
        finding.EndUs.Should().Be(S(52));
        finding.Severity.Should().Be(FindingSeverity.Critical, "the fix was lost for 12 s");
        finding.Measured["minFixType"].Should().Be(1);
        finding.Confidence.Should().Be(FindingConfidence.Medium);
        finding.DataCoverage.Should().BeGreaterThan(0.95);
    }

    [Fact]
    public async Task Gps_fix_loss_does_not_apply_without_gps_or_without_any_3d_fix()
    {
        (await new RuleTestContext().RunAsync(new GpsFixLossRule())).Status.Should().Be(RuleResultStatus.NotApplicable);
        var indoor = await new RuleTestContext().Signal("gps.fix_type", _ => 1).RunAsync(new GpsFixLossRule());
        indoor.Status.Should().Be(RuleResultStatus.NotApplicable);
        indoor.Reason.Should().Contain("never reached a 3D fix");
    }

    [Fact]
    public async Task Gps_quality_uses_the_vehicle_parameters_when_present()
    {
        var context = new RuleTestContext()
            .Parameter("AHRS_GPS_MINSATS", 8)
            .Parameter("GPS_HDOP_GOOD", 140)
            .Signal("gps.fix_type", _ => 3)
            .Signal("gps.satellites", t => t is > 20 and < 30 ? 7 : 12)
            .Signal("gps.hdop", t => t is > 60 and < 63 ? 1.6 : 0.8);

        var result = await context.RunAsync(new GpsQualityRule());

        var sats = result.Findings.Should().ContainSingle(f => f.Title == "Few GPS satellites").Which;
        sats.Threshold["minSatellites"].Should().Be(8);
        sats.ThresholdSource.Should().Contain("AHRS_GPS_MINSATS");
        sats.Confidence.Should().Be(FindingConfidence.Medium, "full coverage, but the log has no arming information");
        sats.Limitations.Should().NotBeEmpty();
        result.Findings.Should().NotContain(f => f.Title == "High GPS HDOP", "the HDOP excursion lasted less than 5 s");
    }

    [Fact]
    public async Task Ekf_rejections_of_all_cores_are_merged_per_measurement()
    {
        var result = await new RuleTestContext()
            .Signal("XKF4.SV", t => t is >= 10 and < 14 ? 1.5 : 0.2)
            .Signal("XKF4#1.SV", t => t is >= 12 and < 16 ? 2.5 : 0.2)
            .Signal("XKF4.SM", _ => 0.1)
            .RunAsync(new EkfInnovationRule());

        var finding = result.Findings.Should().ContainSingle().Which;
        finding.Title.Should().Be("EKF rejected velocity measurements");
        finding.StartUs.Should().Be(S(10));
        finding.EndUs.Should().Be(S(16));
        finding.Signals.Should().BeEquivalentTo("XKF4.SV", "XKF4#1.SV");
        finding.Measured["peakTestRatio"].Should().Be(2.5);
        finding.OccurrenceCount.Should().Be(2);
        result.Signals.Should().Contain("XKF4.SM");
    }

    [Fact]
    public async Task Ekf_rule_does_not_apply_without_test_ratios()
    {
        var result = await new RuleTestContext { Autopilot = Autopilot.Px4 }.Signal("XKF4.SV", _ => 5).RunAsync(new EkfInnovationRule());

        result.Status.Should().Be(RuleResultStatus.NotApplicable, "XKF4 is not a PX4 message");
    }

    [Fact]
    public async Task Ekf_messages_group_repeats_and_keep_the_autopilot_severity()
    {
        var result = await new RuleTestContext()
            .Event(10, FlightEventKind.Message, "EKF3 IMU0 is using GPS", "MSG")
            .Event(10.2, FlightEventKind.Message, "EKF3 IMU1 is using GPS", "MSG")
            .Event(30, FlightEventKind.Error, "EKFCHECK error 2", data: new() { ["subsystem"] = "16", ["code"] = "2" })
            .Event(35, FlightEventKind.Error, "EKFCHECK cleared", data: new() { ["subsystem"] = "16", ["code"] = "0" })
            .Event(40, FlightEventKind.Error, "EKF_PRIMARY error 1", data: new() { ["subsystem"] = "24", ["code"] = "1" })
            .Event(50, FlightEventKind.Message, "Takeoff", "MSG")
            .RunAsync(new EkfMessagesRule());

        result.Findings.Select(f => (f.Title, f.Severity, f.OccurrenceCount)).Should().Equal(
            ("EKF message", FindingSeverity.Info, 2),
            ("EKF message", FindingSeverity.Warning, 1),
            ("EKF primary core changed", FindingSeverity.Info, 1));
        result.Findings.Should().OnlyContain(f => f.Confidence == FindingConfidence.High);
    }

    [Fact]
    public async Task Vibration_uses_documented_ardupilot_limits_and_does_not_apply_to_px4()
    {
        var context = new RuleTestContext()
            .Armed(0, 100)
            .Signal("VIBE.VibeX", t => t is >= 20 and < 25 ? 45 : 10)
            .Signal("VIBE.VibeZ", t => t is >= 24 and < 27 ? 65 : 10)
            .Signal("VIBE.VibeY", t => t is >= 70 and < 70.5 ? 90 : 10);

        var result = await context.RunAsync(new VibrationLevelRule());

        var finding = result.Findings.Should().ContainSingle("the 0.5 s spike is shorter than 1 s, the other two overlap").Which;
        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.StartUs.Should().Be(S(20));
        finding.EndUs.Should().Be(S(27));
        finding.Measured.Should().Contain("max:VIBE.VibeX", 45).And.Contain("max:VIBE.VibeZ", 65);

        var px4 = await new RuleTestContext { Autopilot = Autopilot.Px4 }.RunAsync(new VibrationLevelRule());
        px4.Status.Should().Be(RuleResultStatus.NotApplicable);
    }

    [Fact]
    public async Task Clipping_reports_counter_increases_with_their_total()
    {
        var result = await new RuleTestContext { Autopilot = Autopilot.Px4 }
            .Signal("vehicle_imu_status.accel_clipping[0]", t => t < 30 ? 0 : t < 31 ? 4 : 7, periodS: 0.5)
            .Signal("vehicle_imu_status#1.accel_clipping[2]", _ => 12, periodS: 0.5)
            .RunAsync(new AccelerometerClippingRule());

        var finding = result.Findings.Should().ContainSingle().Which;
        finding.Measured["clippingCount"].Should().Be(7);
        finding.Signals.Should().Equal("vehicle_imu_status.accel_clipping[0]");
        result.Signals.Should().HaveCount(2);
    }

    [Fact]
    public async Task Battery_voltage_follows_ardupilot_failsafe_parameters()
    {
        var context = new RuleTestContext()
            .Parameter("BATT_LOW_VOLT", 10.5)
            .Parameter("BATT_CRT_VOLT", 10.0)
            .Parameter("BATT_LOW_TIMER", 5)
            .Signal("battery.voltage", t => t < 50 ? 12 : t < 58 ? 10.3 : t < 60 ? 12 : 9.8, periodS: 0.5);

        var result = await context.RunAsync(new BatteryVoltageRule());

        result.Findings.Select(f => (f.Severity, f.StartUs)).Should().Equal(
            (FindingSeverity.Warning, S(50)),
            (FindingSeverity.Critical, S(60)));
        result.Findings[1].ThresholdSource.Should().Contain("BATT_CRT_VOLT");

        var unconfigured = await new RuleTestContext().Signal("battery.voltage", _ => 9).RunAsync(new BatteryVoltageRule());
        unconfigured.Status.Should().Be(RuleResultStatus.NotApplicable);
    }

    [Fact]
    public async Task Battery_remaining_reports_the_first_crossing_of_each_px4_level()
    {
        var result = await new RuleTestContext { Autopilot = Autopilot.Px4 }
            .Parameter("BAT_LOW_THR", 0.15)
            .Parameter("BAT_CRIT_THR", 0.07)
            .Signal("battery.remaining", t => 100 - t, periodS: 0.5)
            .RunAsync(new BatteryRemainingRule());

        result.Findings.Select(f => (f.Severity, f.Threshold["remainingPercent"])).Should().Equal(
            (FindingSeverity.Warning, 15.0),
            (FindingSeverity.Critical, 7.0));
        result.Findings[0].StartUs.Should().BeLessThan(result.Findings[1].StartUs);
    }

    [Fact]
    public async Task Failsafes_last_until_cleared_and_repeats_are_merged()
    {
        var result = await new RuleTestContext()
            .Event(10, FlightEventKind.Failsafe, "FAILSAFE_BATT error 1", data: new() { ["subsystem"] = "6", ["code"] = "1" })
            .Event(14, FlightEventKind.Failsafe, "FAILSAFE_BATT cleared", data: new() { ["subsystem"] = "6", ["code"] = "0" })
            .Event(18, FlightEventKind.Failsafe, "FAILSAFE_BATT error 1", data: new() { ["subsystem"] = "6", ["code"] = "1" })
            .Event(60, FlightEventKind.Failsafe, "Failsafe active", "vehicle_status", data: new() { ["active"] = "true" })
            .RunAsync(new FailsafeRule());

        result.Findings.Should().HaveCount(2);
        var battery = result.Findings[0];
        battery.OccurrenceCount.Should().Be(2);
        battery.StartUs.Should().Be(S(10));
        battery.EndUs.Should().Be(S(100), "the second trigger was never cleared, so it lasts to the end of the analyzed time");
        battery.Severity.Should().Be(FindingSeverity.Critical);
        result.Findings[1].Limitations.Should().Contain("not which one");
    }

    [Fact]
    public async Task Error_rule_skips_cleared_ekf_and_unrated_messages()
    {
        var result = await new RuleTestContext()
            .Event(5, FlightEventKind.Error, "CRASH_CHECK error 1", data: new() { ["subsystem"] = "12", ["code"] = "1" })
            .Event(6, FlightEventKind.Error, "GPS cleared", data: new() { ["subsystem"] = "11", ["code"] = "0" })
            .Event(7, FlightEventKind.Error, "EKFCHECK error 2", data: new() { ["subsystem"] = "16", ["code"] = "2" })
            .Event(8, FlightEventKind.Message, "[sensors] no barometer found", "log", EventSeverity.Error)
            .Event(9, FlightEventKind.Message, "PreArm: something", "MSG")
            .RunAsync(new ErrorEventsRule());

        result.Findings.Select(f => (f.Title, f.Severity)).Should().Equal(
            ("Subsystem error", FindingSeverity.Critical),
            ("Error message", FindingSeverity.Warning));
    }

    [Fact]
    public async Task Mode_rule_reports_return_modes_in_flight_and_rapid_switching()
    {
        static Dictionary<string, string> Mode(string name, string? reason = null) =>
            reason is null ? new() { ["mode"] = name } : new() { ["mode"] = name, ["reason"] = reason };

        var result = await new RuleTestContext()
            .Armed(10, 90)
            .Event(5, FlightEventKind.ModeChange, "Mode RTL", "MODE", data: Mode("RTL"))
            .Event(20, FlightEventKind.ModeChange, "Mode LOITER", "MODE", data: Mode("LOITER"))
            .Event(21, FlightEventKind.ModeChange, "Mode ALT_HOLD", "MODE", data: Mode("ALT_HOLD"))
            .Event(22, FlightEventKind.ModeChange, "Mode LOITER", "MODE", data: Mode("LOITER"))
            .Event(23, FlightEventKind.ModeChange, "Mode ALT_HOLD", "MODE", data: Mode("ALT_HOLD"))
            .Event(60, FlightEventKind.ModeChange, "Mode RTL", "MODE", data: Mode("RTL", "3"))
            .RunAsync(new ModeTransitionRule());

        result.Findings.Select(f => f.Title).Should().Equal("Rapid mode switching", "RTL mode engaged");
        result.Findings[0].Measured["modeChanges"].Should().Be(4);
        result.Findings[1].Measured["modeReason"].Should().Be(3);
        result.Findings[1].Limitations.Should().BeNull();

        (await new RuleTestContext().RunAsync(new ModeTransitionRule())).Status.Should().Be(RuleResultStatus.NotApplicable);
    }

    [Fact]
    public async Task Parameter_changes_while_armed_are_grouped_with_old_and_new_value()
    {
        var result = await new RuleTestContext()
            .Armed(10, 90)
            .Parameter("ATC_RAT_RLL_P", 0.1)
            .Parameter("ATC_RAT_RLL_P", 0.15, changedAtS: 5)
            .Parameter("ATC_RAT_RLL_P", 0.2, changedAtS: 30)
            .Parameter("ATC_RAT_PIT_P", 0.12, changedAtS: 32)
            .RunAsync(new ParameterChangeRule());

        var finding = result.Findings.Should().ContainSingle().Which;
        finding.Description.Should().Contain("ATC_RAT_RLL_P 0.15 → 0.2").And.Contain("ATC_RAT_PIT_P ? → 0.12");
        finding.Measured.Should().Contain("ATC_RAT_RLL_P", 0.2);
        finding.OccurrenceCount.Should().Be(2);
    }

    [Fact]
    public async Task Data_gaps_of_several_signals_merge_and_logger_dropouts_are_included()
    {
        var context = new RuleTestContext()
            .Armed(0, 100)
            .Signal("attitude.roll", t => 0, periodS: 0.02)
            .Signal("IMU.GyrX", t => 0, periodS: 0.01)
            .Event(70, FlightEventKind.Dropout, "Logger dropout of 300 ms", "logger", data: new() { ["durationMs"] = "300" });

        // Remove 3 s of attitude and 2.5 s of IMU data that overlap.
        var roll = (await context.LoadAsync("attitude.roll", CancellationToken.None))!;
        var keepRoll = roll.Timestamps.Select((t, i) => (t, i)).Where(x => x.t < S(40) || x.t > S(43)).Select(x => x.i).ToArray();
        context.Signal("attitude.roll", [.. keepRoll.Select(i => roll.Timestamps[i])], [.. keepRoll.Select(i => roll.Values[i])]);
        var imu = (await context.LoadAsync("IMU.GyrX", CancellationToken.None))!;
        var keepImu = imu.Timestamps.Select((t, i) => (t, i)).Where(x => x.t < S(41) || x.t > S(43.5)).Select(x => x.i).ToArray();
        context.Signal("IMU.GyrX", [.. keepImu.Select(i => imu.Timestamps[i])], [.. keepImu.Select(i => imu.Values[i])]);

        var result = await context.RunAsync(new DataGapRule());

        result.Findings.Should().HaveCount(2);
        var gap = result.Findings[0];
        gap.Signals.Should().BeEquivalentTo("attitude.roll", "IMU.GyrX");
        gap.Severity.Should().Be(FindingSeverity.Warning);
        gap.Measured["durationS"].Should().BeApproximately(3.5, 0.05);
        result.Findings[1].Title.Should().Be("Logger dropout");
        result.Findings[1].Severity.Should().Be(FindingSeverity.Info);
    }
}
