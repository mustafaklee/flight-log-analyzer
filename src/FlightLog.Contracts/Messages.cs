namespace FlightLog.Contracts;

/// <summary>
/// A message on the processing queue. The broker message carries <see cref="MessageTypes"/> in its <c>type</c>
/// property and this payload as JSON. A version is part of the type name: an incompatible change gets a new
/// type, and consumers keep reading the old one until no producer sends it anymore.
/// </summary>
public interface IWorkMessage
{
    /// <summary>Ties the log lines of every step of one piece of work together.</summary>
    string CorrelationId { get; }
}

/// <summary>Parse a queued log file. Sent when a file enters <c>Queued</c> (upload, user retry, automatic retry).</summary>
public sealed record ProcessLogFileV1(Guid LogFileId, Guid TeamId, string CorrelationId) : IWorkMessage;

/// <summary>Run the analysis rules of a queued analysis run.</summary>
public sealed record AnalyzeFlightV1(Guid AnalysisRunId, Guid FlightId, Guid TeamId, string CorrelationId) : IWorkMessage;

public static class MessageTypes
{
    public const string ProcessLogFileV1 = "flightlog.process-log-file.v1";
    public const string AnalyzeFlightV1 = "flightlog.analyze-flight.v1";

    public static string Of(IWorkMessage message) => message switch
    {
        Contracts.ProcessLogFileV1 => ProcessLogFileV1,
        Contracts.AnalyzeFlightV1 => AnalyzeFlightV1,
        _ => throw new ArgumentException($"Unknown message {message?.GetType().Name}.", nameof(message)),
    };

    public static Type? ClrTypeOf(string? type) => type switch
    {
        ProcessLogFileV1 => typeof(ProcessLogFileV1),
        AnalyzeFlightV1 => typeof(AnalyzeFlightV1),
        _ => null,
    };
}
