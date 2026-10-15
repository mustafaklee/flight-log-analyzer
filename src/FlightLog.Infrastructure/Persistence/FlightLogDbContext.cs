using FlightLog.Application.Abstractions;
using FlightLog.Application.Messaging;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Fleet;
using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Projects;
using FlightLog.Domain.Teams;
using FlightLog.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Infrastructure.Persistence;

public sealed class FlightLogDbContext(DbContextOptions<FlightLogDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext, IDataProtectionKeyContext
{
    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Aircraft> Aircraft => Set<Aircraft>();

    public DbSet<Flight> Flights => Set<Flight>();

    public DbSet<FlightLogFile> FlightLogFiles => Set<FlightLogFile>();

    public DbSet<LogProcessingJob> LogProcessingJobs => Set<LogProcessingJob>();

    public DbSet<FlightSignalSeries> FlightSignalSeries => Set<FlightSignalSeries>();

    public DbSet<SignalChunkInfo> SignalChunks => Set<SignalChunkInfo>();

    public DbSet<FlightSignal> FlightSignals => Set<FlightSignal>();

    public DbSet<FlightEvent> FlightEvents => Set<FlightEvent>();

    public DbSet<FlightParameter> FlightParameters => Set<FlightParameter>();

    public DbSet<AnalysisRun> AnalysisRuns => Set<AnalysisRun>();

    public DbSet<AnalysisFinding> AnalysisFindings => Set<AnalysisFinding>();

    public DbSet<AnalysisRuleResult> AnalysisRuleResults => Set<AnalysisRuleResult>();

    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();

    public DbSet<FindingReview> FindingReviews => Set<FindingReview>();

    public DbSet<FlightComment> FlightComments => Set<FlightComment>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(FlightLogDbContext).Assembly);
    }
}
