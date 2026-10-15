using System.Globalization;
using System.Text.RegularExpressions;

namespace FlightLog.LogParsing.Interpretation;

/// <summary>
/// Reads flight-level facts from ArduPilot log messages (VER, MSG, MODE, ARM, EV, ERR, PARM, GPS).
/// Mode and event names come from the ArduPilot sources (mode.h of each vehicle, AP_Logger LogEvent and
/// LogErrorSubsystem); numbers without a well-established name are reported as numbers.
/// </summary>
public sealed partial class ArduPilotInterpreter : FlightInterpreter
{
    private static readonly Dictionary<string, Dictionary<int, string>> Modes = new(StringComparer.Ordinal)
    {
        ["Copter"] = new()
        {
            [0] = "STABILIZE",
            [1] = "ACRO",
            [2] = "ALT_HOLD",
            [3] = "AUTO",
            [4] = "GUIDED",
            [5] = "LOITER",
            [6] = "RTL",
            [7] = "CIRCLE",
            [9] = "LAND",
            [11] = "DRIFT",
            [13] = "SPORT",
            [14] = "FLIP",
            [15] = "AUTOTUNE",
            [16] = "POSHOLD",
            [17] = "BRAKE",
            [18] = "THROW",
            [19] = "AVOID_ADSB",
            [20] = "GUIDED_NOGPS",
            [21] = "SMART_RTL",
            [22] = "FLOWHOLD",
            [23] = "FOLLOW",
            [24] = "ZIGZAG",
            [25] = "SYSTEMID",
            [26] = "AUTOROTATE",
            [27] = "AUTO_RTL",
            [28] = "TURTLE",
        },
        ["Plane"] = new()
        {
            [0] = "MANUAL",
            [1] = "CIRCLE",
            [2] = "STABILIZE",
            [3] = "TRAINING",
            [4] = "ACRO",
            [5] = "FBWA",
            [6] = "FBWB",
            [7] = "CRUISE",
            [8] = "AUTOTUNE",
            [10] = "AUTO",
            [11] = "RTL",
            [12] = "LOITER",
            [13] = "TAKEOFF",
            [14] = "AVOID_ADSB",
            [15] = "GUIDED",
            [17] = "QSTABILIZE",
            [18] = "QHOVER",
            [19] = "QLOITER",
            [20] = "QLAND",
            [21] = "QRTL",
            [22] = "QAUTOTUNE",
            [23] = "QACRO",
            [24] = "THERMAL",
            [25] = "LOITER_ALT_QLAND",
        },
        ["Rover"] = new()
        {
            [0] = "MANUAL",
            [1] = "ACRO",
            [3] = "STEERING",
            [4] = "HOLD",
            [5] = "LOITER",
            [6] = "FOLLOW",
            [7] = "SIMPLE",
            [10] = "AUTO",
            [11] = "RTL",
            [12] = "SMART_RTL",
            [15] = "GUIDED",
        },
    };

    private static readonly Dictionary<int, string> EventNames = new()
    {
        [10] = "ARMED",
        [11] = "DISARMED",
        [15] = "AUTO_ARMED",
        [17] = "LAND_COMPLETE_MAYBE",
        [18] = "LAND_COMPLETE",
        [19] = "LOST_GPS",
        [21] = "FLIP_START",
        [22] = "FLIP_END",
        [25] = "SET_HOME",
        [26] = "SET_SIMPLE_ON",
        [27] = "SET_SIMPLE_OFF",
        [28] = "NOT_LANDED",
        [29] = "SET_SUPERSIMPLE_ON",
        [30] = "AUTOTUNE_INITIALISED",
        [31] = "AUTOTUNE_OFF",
        [32] = "AUTOTUNE_RESTART",
        [33] = "AUTOTUNE_SUCCESS",
        [34] = "AUTOTUNE_FAILED",
        [35] = "AUTOTUNE_REACHED_LIMIT",
        [36] = "AUTOTUNE_PILOT_TESTING",
        [37] = "AUTOTUNE_SAVEDGAINS",
        [38] = "SAVE_TRIM",
        [39] = "SAVEWP_ADD_WP",
        [41] = "FENCE_ENABLE",
        [42] = "FENCE_DISABLE",
        [43] = "ACRO_TRAINER_OFF",
        [44] = "ACRO_TRAINER_LEVELING",
        [45] = "ACRO_TRAINER_LIMITED",
        [46] = "GRIPPER_GRAB",
        [47] = "GRIPPER_RELEASE",
        [49] = "PARACHUTE_DISABLED",
        [50] = "PARACHUTE_ENABLED",
        [51] = "PARACHUTE_RELEASED",
        [52] = "LANDING_GEAR_DEPLOYED",
        [53] = "LANDING_GEAR_RETRACTED",
        [54] = "MOTORS_EMERGENCY_STOPPED",
        [55] = "MOTORS_EMERGENCY_STOP_CLEARED",
        [56] = "MOTORS_INTERLOCK_DISABLED",
        [57] = "MOTORS_INTERLOCK_ENABLED",
        [58] = "ROTOR_RUNUP_COMPLETE",
        [59] = "ROTOR_SPEED_BELOW_CRITICAL",
        [60] = "EKF_ALT_RESET",
        [61] = "LAND_CANCELLED_BY_PILOT",
        [62] = "EKF_YAW_RESET",
        [63] = "AVOIDANCE_ADSB_ENABLE",
        [64] = "AVOIDANCE_ADSB_DISABLE",
        [65] = "AVOIDANCE_PROXIMITY_ENABLE",
        [66] = "AVOIDANCE_PROXIMITY_DISABLE",
        [67] = "GPS_PRIMARY_CHANGED",
        [71] = "ZIGZAG_STORE_A",
        [72] = "ZIGZAG_STORE_B",
        [73] = "LAND_REPO_ACTIVE",
        [74] = "STANDBY_ENABLE",
        [75] = "STANDBY_DISABLE",
        [76] = "FENCE_ALT_MAX_ENABLE",
        [77] = "FENCE_ALT_MAX_DISABLE",
        [78] = "FENCE_CIRCLE_ENABLE",
        [79] = "FENCE_CIRCLE_DISABLE",
        [80] = "FENCE_ALT_MIN_ENABLE",
        [81] = "FENCE_ALT_MIN_DISABLE",
        [82] = "FENCE_POLYGON_ENABLE",
        [83] = "FENCE_POLYGON_DISABLE",
        [85] = "EK3_SOURCES_SET_TO_PRIMARY",
        [86] = "EK3_SOURCES_SET_TO_SECONDARY",
        [87] = "EK3_SOURCES_SET_TO_TERTIARY",
        [90] = "AIRSPEED_PRIMARY_CHANGED",
        [163] = "SURFACED",
        [164] = "NOT_SURFACED",
        [165] = "BOTTOMED",
        [166] = "NOT_BOTTOMED",
    };

    private static readonly Dictionary<int, string> ErrorSubsystems = new()
    {
        [1] = "MAIN",
        [2] = "RADIO",
        [3] = "COMPASS",
        [4] = "OPTFLOW",
        [5] = "FAILSAFE_RADIO",
        [6] = "FAILSAFE_BATT",
        [7] = "FAILSAFE_GPS",
        [8] = "FAILSAFE_GCS",
        [9] = "FAILSAFE_FENCE",
        [10] = "FLIGHT_MODE",
        [11] = "GPS",
        [12] = "CRASH_CHECK",
        [13] = "FLIP",
        [14] = "AUTOTUNE",
        [15] = "PARACHUTES",
        [16] = "EKFCHECK",
        [17] = "FAILSAFE_EKFINAV",
        [18] = "BARO",
        [19] = "CPU",
        [20] = "FAILSAFE_ADSB",
        [21] = "TERRAIN",
        [22] = "NAVIGATION",
        [23] = "FAILSAFE_TERRAIN",
        [24] = "EKF_PRIMARY",
        [25] = "THRUST_LOSS_CHECK",
    };

    private readonly Dictionary<string, double> _parameterValues = new(StringComparer.Ordinal);
    private readonly List<FlightEventRecord> _eventArming = [];
    private bool _hasArmMessages;
    private string? _vehicle;

    public override void OnRecord(LogRecord record)
    {
        switch (record.Series.Message)
        {
            case "VER":
                OnVersion(record);
                break;
            case "MSG":
                OnMessage(record);
                break;
            case "PARM":
                OnParameterRecord(record);
                break;
            case "MODE":
                OnMode(record);
                break;
            case "ARM":
                OnArm(record);
                break;
            case "EV":
                OnEvent(record);
                break;
            case "ERR":
                OnError(record);
                break;
            case "GPS" when record.Series.Instance == 0 && Summary.BootTimeUtc is null:
                OnGps(record);
                break;
        }
    }

    public override FlightSummary Complete(ParserReport report)
    {
        // Older firmware has no ARM message and logs arming as events 10/11 instead.
        if (!_hasArmMessages)
        {
            foreach (var e in _eventArming)
            {
                AddEvent(e);
            }
        }

        Summary.VehicleType = _vehicle;
        return base.Complete(report);
    }

    private void OnVersion(LogRecord record)
    {
        if (record.Text("FWS") is { Length: > 0 } fws)
        {
            Summary.FirmwareVersion ??= fws;
            _vehicle ??= VehicleFrom(fws);
        }

        SetMetadata("board_type", Number(record.Value("BT")));
        if (record.Value("GH") is var hash && !double.IsNaN(hash))
        {
            SetMetadata("git_hash", ((uint)hash).ToString("x8", CultureInfo.InvariantCulture));
        }
    }

    private void OnMessage(LogRecord record)
    {
        var text = record.Text("Message") ?? string.Empty;
        if (Summary.FirmwareVersion is null && FirmwarePattern().Match(text) is { Success: true } match)
        {
            Summary.FirmwareVersion = match.Value.Trim();
            _vehicle ??= VehicleFrom(text);
        }

        if (text.StartsWith("Frame: ", StringComparison.Ordinal))
        {
            SetMetadata("frame", text["Frame: ".Length..]);
        }

        AddEvent(new FlightEventRecord(record.TimestampUs, FlightEventKind.Message, null, "MSG", text));
    }

    private void OnParameterRecord(LogRecord record)
    {
        var name = record.Text("Name");
        var value = record.Value("Value");
        if (string.IsNullOrEmpty(name) || double.IsNaN(value))
        {
            return;
        }

        var defaultValue = record.Value("Default");
        double? @default = double.IsNaN(defaultValue) ? null : defaultValue;
        if (!_parameterValues.TryGetValue(name, out var previous))
        {
            AddParameter(new LogParameter(name, value, null, @default));
        }
        else if (!previous.Equals(value))
        {
            AddParameter(new LogParameter(name, value, record.TimestampUs, @default));
        }

        _parameterValues[name] = value;
    }

    private void OnMode(LogRecord record)
    {
        var mode = record.Value("Mode");
        if (double.IsNaN(mode))
        {
            return;
        }

        var number = (int)mode;
        var name = _vehicle is not null && Modes.TryGetValue(_vehicle, out var table) && table.TryGetValue(number, out var known) ? known : $"mode {number}";
        var data = new Dictionary<string, string> { ["mode"] = name, ["modeNumber"] = Number(mode)! };
        if (Number(record.Value("Rsn")) is { } reason)
        {
            data["reason"] = reason;
        }

        AddEvent(new FlightEventRecord(record.TimestampUs, FlightEventKind.ModeChange, LogSeverity.Info, "MODE", $"Mode {name}", data));
    }

    private void OnArm(LogRecord record)
    {
        _hasArmMessages = true;
        var state = record.Value("ArmState");
        if (double.IsNaN(state))
        {
            return;
        }

        var armed = state != 0;
        var data = new Dictionary<string, string>();
        if (Number(record.Value("Method")) is { } method)
        {
            data["method"] = method;
        }

        if (Number(record.Value("Forced")) is { } forced)
        {
            data["forced"] = forced;
        }

        AddEvent(new FlightEventRecord(record.TimestampUs, armed ? FlightEventKind.Armed : FlightEventKind.Disarmed, LogSeverity.Info, "ARM",
            armed ? "Armed" : "Disarmed", data));
    }

    private void OnEvent(LogRecord record)
    {
        var id = record.Value("Id");
        if (double.IsNaN(id))
        {
            return;
        }

        var number = (int)id;
        var name = EventNames.GetValueOrDefault(number);
        var data = new Dictionary<string, string> { ["eventId"] = Number(id)! };
        if (number is 10 or 11)
        {
            _eventArming.Add(new FlightEventRecord(record.TimestampUs, number == 10 ? FlightEventKind.Armed : FlightEventKind.Disarmed,
                LogSeverity.Info, "EV", number == 10 ? "Armed" : "Disarmed", data));
            return;
        }

        AddEvent(new FlightEventRecord(record.TimestampUs, FlightEventKind.Event, LogSeverity.Info, "EV", name ?? $"Event {number}", data));
    }

    private void OnError(LogRecord record)
    {
        var subsystem = record.Value("Subsys");
        var code = record.Value("ECode");
        if (double.IsNaN(subsystem) || double.IsNaN(code))
        {
            return;
        }

        var name = ErrorSubsystems.TryGetValue((int)subsystem, out var known) ? known : $"subsystem {(int)subsystem}";
        var isFailsafe = name.StartsWith("FAILSAFE_", StringComparison.Ordinal);
        var cleared = code == 0;
        var kind = isFailsafe ? FlightEventKind.Failsafe : FlightEventKind.Error;
        var severity = cleared ? LogSeverity.Info : (isFailsafe ? LogSeverity.Critical : LogSeverity.Error);
        var text = cleared ? $"{name} cleared" : $"{name} error {(int)code}";
        AddEvent(new FlightEventRecord(record.TimestampUs, kind, severity, "ERR", text,
            new Dictionary<string, string> { ["subsystem"] = Number(subsystem)!, ["code"] = Number(code)! }));
    }

    private void OnGps(LogRecord record)
    {
        var status = record.Value("Status");
        var week = record.Value("GWk");
        var ms = record.Value("GMS");
        if (double.IsNaN(status) || status < 3 || double.IsNaN(week) || week <= 0 || double.IsNaN(ms))
        {
            return;
        }

        var utc = GpsToUtc((long)week, ms);
        Summary.BootTimeUtc = utc.AddTicks(-record.TimestampUs * 10);
        SetMetadata("time_source", "gps");
    }

    private static string? VehicleFrom(string firmware) => firmware switch
    {
        _ when firmware.StartsWith("ArduCopter", StringComparison.Ordinal) || firmware.StartsWith("APM:Copter", StringComparison.Ordinal) => "Copter",
        _ when firmware.StartsWith("ArduPlane", StringComparison.Ordinal) || firmware.StartsWith("APM:Plane", StringComparison.Ordinal) => "Plane",
        _ when firmware.StartsWith("ArduRover", StringComparison.Ordinal) || firmware.StartsWith("APM:Rover", StringComparison.Ordinal) || firmware.StartsWith("Rover", StringComparison.Ordinal) => "Rover",
        _ when firmware.StartsWith("ArduSub", StringComparison.Ordinal) => "Sub",
        _ when firmware.StartsWith("AntennaTracker", StringComparison.Ordinal) => "AntennaTracker",
        _ when firmware.StartsWith("Blimp", StringComparison.Ordinal) => "Blimp",
        _ => null,
    };

    private static string? Number(double value) =>
        double.IsNaN(value) ? null : value.ToString("R", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(ArduCopter|ArduPlane|ArduRover|ArduSub|AntennaTracker|Blimp|Rover|APM:\w+)\s+V?\d+\.\d+[^\s]*(\s+\([0-9a-f]+\))?")]
    private static partial Regex FirmwarePattern();
}
