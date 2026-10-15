using FlightLog.Domain.Fleet;
using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Projects;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FlightLog.Application.Abstractions;

/// <summary>
/// The data access surface the application layer needs. EF Core's DbContext already acts as unit of work
/// and DbSet as repository, so no additional repository layer is introduced (see ADR-0002).
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Team> Teams { get; }

    DbSet<TeamMember> TeamMembers { get; }

    DbSet<Project> Projects { get; }

    DbSet<Aircraft> Aircraft { get; }

    DbSet<Flight> Flights { get; }

    DbSet<FlightLogFile> FlightLogFiles { get; }

    DbSet<LogProcessingJob> LogProcessingJobs { get; }

    DbSet<FlightSignalSeries> FlightSignalSeries { get; }

    DbSet<SignalChunkInfo> SignalChunks { get; }

    DbSet<FlightSignal> FlightSignals { get; }

    DbSet<FlightEvent> FlightEvents { get; }

    DbSet<FlightParameter> FlightParameters { get; }

    /// <summary>Transactions for multi-statement writes (bulk delete + insert of derived flight data).</summary>
    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
