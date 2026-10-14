using FlightLog.Domain.Projects;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;

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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
