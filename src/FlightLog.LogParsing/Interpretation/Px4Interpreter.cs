using System.Globalization;

namespace FlightLog.LogParsing.Interpretation;

/// <summary>
/// Reads flight-level facts from PX4 uORB topics. Field and enum names follow the PX4 message definitions
/// (msg/VehicleStatus.msg, msg/SensorGps.msg); values whose meaning changed between PX4 versions are reported
/// with their raw number only.
/// </summary>
public sealed class Px4Interpreter : FlightInterpreter
{
    // vehicle_status.nav_state values that have kept their meaning across PX4 releases.
    private static readonly Dictionary<int, string> NavStates = new()
    {
        [0] = "MANUAL",
        [1] = "ALTCTL",
        [2] = "POSCTL",
        [3] = "AUTO_MISSION",
        [4] = "AUTO_LOITER",
        [5] = "AUTO_RTL",
        [10] = "ACRO",
        [12] = "DESCEND",
        [13] = "TERMINATION",
        [14] = "OFFBOARD",
        [15] = "STAB",
        [17] = "AUTO_TAKEOFF",
        [18] = "AUTO_LAND",
        [19] = "AUTO_FOLLOW_TARGET",
        [20] = "AUTO_PRECLAND",
        [21] = "ORBIT",
    };

    private static readonly Dictionary<int, string> VehicleTypes = new()
    {
        [1] = "RotaryWing",
        [2] = "FixedWing",
        [3] = "Rover",
        [4] = "Airship",
    };

    private const int ArmingStateArmed = 2;

    private readonly Dictionary<string, string> _info = new(StringComparer.Ordinal);
    private int? _navState;
    private bool? _armed;
    private bool? _failsafe;
    private bool _vtol;

    public override void OnInfo(string key, string value)
    {
        _info[key] = value;
        SetMetadata(key, value);
    }

    public override void OnText(LogText text) =>
        AddEvent(new FlightEventRecord(text.TimestampUs, FlightEventKind.Message, text.Severity, "log", text.Message));

    public override void OnParameter(LogParameter parameter)
    {
        // Default-value messages carry no current value; merge them into the initial value.
        if (double.IsNaN(parameter.Value))
        {
            var index = Summary.Parameters.FindIndex(p => p.Name == parameter.Name && p.TimestampUs is null);
            if (index >= 0)
            {
                Summary.Parameters[index] = Summary.Parameters[index] with { DefaultValue = parameter.DefaultValue };
            }

            return;
        }

        AddParameter(parameter);
    }

    public override void OnRecord(LogRecord record)
    {
        switch (record.Series.Message)
        {
            case "vehicle_status" when record.Series.Instance == 0:
                OnVehicleStatus(record);
                break;
            case "vehicle_gps_position" or "sensor_gps" when Summary.BootTimeUtc is null:
                OnGps(record);
                break;
        }
    }

    public override FlightSummary Complete(ParserReport report)
    {
        Summary.FirmwareVersion = FirmwareVersion();
        Summary.Hardware = _info.GetValueOrDefault("ver_hw");
        if (_vtol && Summary.VehicleType is not null)
        {
            Summary.VehicleType = "Vtol";
        }

        var summary = base.Complete(report);

        // Topics that were never published carry timestamp 0, so the earliest record is not the start of logging.
        // The header timestamp is when the logger started (pyulog uses it as start_timestamp too).
        if (report.HeaderTimestampUs > 0)
        {
            summary.FirstTimestampUs = report.HeaderTimestampUs;
        }

        return summary;
    }

    private void OnVehicleStatus(LogRecord record)
    {
        var navState = record.Value("nav_state");
        if (!double.IsNaN(navState) && (int)navState != _navState)
        {
            var value = (int)navState;
            var name = NavStates.TryGetValue(value, out var known) ? known : $"nav_state {value}";
            AddEvent(new FlightEventRecord(record.TimestampUs, FlightEventKind.ModeChange, LogSeverity.Info, "vehicle_status",
                $"Mode {name}", new Dictionary<string, string> { ["navState"] = Invariant(value), ["mode"] = name }));
            _navState = value;
        }

        var arming = record.Value("arming_state");
        if (!double.IsNaN(arming))
        {
            var armed = (int)arming == ArmingStateArmed;
            if (armed != _armed && (_armed is not null || armed))
            {
                AddEvent(new FlightEventRecord(record.TimestampUs, armed ? FlightEventKind.Armed : FlightEventKind.Disarmed, LogSeverity.Info,
                    "vehicle_status", armed ? "Armed" : "Disarmed", new Dictionary<string, string> { ["armingState"] = Invariant((int)arming) }));
            }

            _armed = armed;
        }

        var failsafe = record.Value("failsafe");
        if (!double.IsNaN(failsafe))
        {
            var active = failsafe != 0;
            if (active != _failsafe && (_failsafe is not null || active))
            {
                AddEvent(new FlightEventRecord(record.TimestampUs, FlightEventKind.Failsafe, active ? LogSeverity.Critical : LogSeverity.Info,
                    "vehicle_status", active ? "Failsafe active" : "Failsafe cleared", new Dictionary<string, string> { ["active"] = active ? "true" : "false" }));
            }

            _failsafe = active;
        }

        var vehicleType = record.Value("vehicle_type");
        if (!double.IsNaN(vehicleType) && VehicleTypes.TryGetValue((int)vehicleType, out var type))
        {
            Summary.VehicleType ??= type;
        }

        _vtol |= record.Value("is_vtol") is var vtol && !double.IsNaN(vtol) && vtol != 0;
    }

    private void OnGps(LogRecord record)
    {
        var utc = record.Value("time_utc_usec");
        var fix = record.Value("fix_type");
        if (double.IsNaN(utc) || utc <= 0 || double.IsNaN(fix) || fix < 3)
        {
            return;
        }

        // Boot time = GPS UTC time of this sample minus its time since boot.
        Summary.BootTimeUtc = DateTimeOffset.UnixEpoch.AddTicks(((long)utc - record.TimestampUs) * 10);
        SetMetadata("time_source", "gps");
    }

    /// <summary>
    /// <c>ver_sw_release</c> encodes major, minor, patch and type (0xAABBCCTT, type 255 = release, ≥192 RC,
    /// ≥128 beta, ≥64 alpha, below = development build).
    /// </summary>
    private string? FirmwareVersion()
    {
        var name = _info.GetValueOrDefault("sys_name") ?? "PX4";
        if (_info.TryGetValue("ver_sw_release", out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            var v = (uint)number;
            var type = v & 0xFF;
            var suffix = type switch
            {
                255 => string.Empty,
                >= 192 => " (RC)",
                >= 128 => " (beta)",
                >= 64 => " (alpha)",
                _ => " (dev)",
            };
            return $"{name} v{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}{suffix}";
        }

        return _info.TryGetValue("ver_sw", out var hash) && hash.Length > 0 ? $"{name} git {hash[..Math.Min(8, hash.Length)]}" : null;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
