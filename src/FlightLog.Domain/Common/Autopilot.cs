namespace FlightLog.Domain.Common;

/// <summary>Flight control software family. <see cref="Unknown"/> is used when it cannot be determined.</summary>
public enum Autopilot
{
    Unknown = 0,
    Px4 = 1,
    ArduPilot = 2,
}
