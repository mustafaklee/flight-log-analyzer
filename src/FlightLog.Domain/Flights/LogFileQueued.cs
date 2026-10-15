using FlightLog.Domain.Common;

namespace FlightLog.Domain.Flights;

/// <summary>A log file entered the processing queue; <see cref="NotBefore"/> delays an automatic retry.</summary>
public sealed record LogFileQueued(Guid LogFileId, Guid TeamId, DateTimeOffset? NotBefore) : IDomainEvent;
