using FlightLog.Domain.Flights;

namespace FlightLog.Application.Logs;

/// <summary>
/// Identifies a log format from the first bytes of the content, never from the file name alone.
/// <list type="bullet">
/// <item>PX4 ULog: file magic <c>"ULog"</c> followed by <c>0x01 0x12 0x35</c>, a version byte and a
/// 64-bit timestamp (PX4 "ULog File Format" documentation).</item>
/// <item>ArduPilot DataFlash: every message starts with the header bytes <c>0xA3 0x95</c> and a message
/// type byte; a log starts with the self-describing FMT message whose type is 128 (<c>0x80</c>).</item>
/// </list>
/// Full structural validation happens in the parsers (stage 3); this check rejects files that are clearly not logs.
/// </summary>
public static class LogFormatDetector
{
    /// <summary>Bytes needed for a decision.</summary>
    public const int HeaderLength = 16;

    private static readonly byte[] ULogMagic = [0x55, 0x4C, 0x6F, 0x67, 0x01, 0x12, 0x35];
    private const byte DataFlashHead1 = 0xA3;
    private const byte DataFlashHead2 = 0x95;
    private const byte DataFlashFmtType = 0x80;

    public static LogFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= HeaderLength && header.StartsWith(ULogMagic))
        {
            return LogFormat.Px4ULog;
        }

        if (header.Length >= 3 && header[0] == DataFlashHead1 && header[1] == DataFlashHead2 && header[2] == DataFlashFmtType)
        {
            return LogFormat.ArduPilotDataFlash;
        }

        return null;
    }

    /// <summary>The format a file extension claims, or null for extensions that are not accepted.</summary>
    public static LogFormat? FromExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".ulg" => LogFormat.Px4ULog,
            ".bin" => LogFormat.ArduPilotDataFlash,
            _ => null,
        };
}
