namespace FlightLog.Domain.Flights;

public enum LogFormat
{
    /// <summary>PX4 ULog (<c>.ulg</c>).</summary>
    Px4ULog = 1,

    /// <summary>ArduPilot DataFlash binary log (<c>.bin</c>).</summary>
    ArduPilotDataFlash = 2,
}

public static class LogFormatExtensions
{
    public static Common.Autopilot ToAutopilot(this LogFormat format) => format switch
    {
        LogFormat.Px4ULog => Common.Autopilot.Px4,
        LogFormat.ArduPilotDataFlash => Common.Autopilot.ArduPilot,
        _ => Common.Autopilot.Unknown,
    };
}
