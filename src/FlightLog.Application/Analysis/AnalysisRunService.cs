using FlightLog.Application.Abstractions;
using FlightLog.Application.Collaboration;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Analysis;

/// <summary>Analysis runs and findings as the API offers them.</summary>
public interface IAnalysisRunService
{
    /// <summary>Queues a new rule analysis of the flight's processed log (Engineer). The worker runs it.</summary>
    Task<AnalysisRunDto> RequestAsync(Guid flightId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AnalysisRunDto>> ListForFlightAsync(Guid flightId, CancellationToken cancellationToken);

    Task<AnalysisRunDto> GetAsync(Guid runId, CancellationToken cancellationToken);

    Task<PagedResult<FindingDto>> ListFindingsAsync(Guid runId, FindingQuery query, CancellationToken cancellationToken);

    IReadOnlyList<RuleDescriptorDto> ListRules();
}

internal sealed class AnalysisRunService(
    IApplicationDbContext db,
    ITeamAccessService access,
    ICurrentUser currentUser,
    IAuditTrail audit,
    IFindingReviewService findingReviews,
    RuleEngine engine,
    TimeProvider clock,
    IValidator<FindingQuery> findingValidator) : IAnalysisRunService
{
    public async Task<AnalysisRunDto> RequestAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await db.Flights.AsNoTracking().SingleOrDefaultAsync(f => f.Id == flightId, cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(flight.TeamId, TeamRole.Engineer, "Flight", flightId, cancellationToken);

        var file = await db.FlightLogFiles.Where(f => f.FlightId == flightId).OrderByDescending(f => f.UploadedAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainRuleViolationException("analysis_run.no_log", "The flight has no log file to analyze.");
        if (file.Status is ProcessingStatus.AnalysisPending or ProcessingStatus.Analyzing)
        {
            throw new ConflictException("analysis_run.already_pending", "An analysis of this flight is already queued or running.");
        }

        var now = clock.GetUtcNow();
        file.RequestAnalysis(now);
        var run = AnalysisRun.Queue(file.TeamId, flightId, file.Id, AnalysisTrigger.Manual, currentUser.UserId, now);
        db.AnalysisRuns.Add(run);
        audit.Record(file.TeamId, AuditActions.AnalysisRequested, AuditEntities.AnalysisRun, run.Id, flightId, flight.Name);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("log_file.status_changed", "The file status changed in the meantime. Reload and try again.");
        }

        return ToDto(run, null);
    }

    public async Task<IReadOnlyList<AnalysisRunDto>> ListForFlightAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var teamId = await db.Flights.AsNoTracking().Where(f => f.Id == flightId).Select(f => (Guid?)f.TeamId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(teamId, TeamRole.Viewer, "Flight", flightId, cancellationToken);
        var runs = await db.AnalysisRuns.AsNoTracking().Where(r => r.FlightId == flightId).OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken);
        return [.. runs.Select(r => ToDto(r, null))];
    }

    public async Task<AnalysisRunDto> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadAuthorizedAsync(runId, cancellationToken);
        var rules = await db.AnalysisRuleResults.AsNoTracking().Where(r => r.RunId == runId).OrderBy(r => r.RuleId).ToListAsync(cancellationToken);
        return ToDto(run, [.. rules.Select(r => new RuleResultDto(r.RuleId, r.RuleVersion, r.Status, r.Reason, r.FindingCount, r.Signals, r.DurationMs))]);
    }

    public async Task<PagedResult<FindingDto>> ListFindingsAsync(Guid runId, FindingQuery query, CancellationToken cancellationToken)
    {
        await findingValidator.EnsureValidAsync(query, cancellationToken);
        var run = await LoadAuthorizedAsync(runId, cancellationToken);

        var findings = db.AnalysisFindings.AsNoTracking().Where(f => f.RunId == runId);
        var reviews = db.FindingReviews.Where(r => r.FlightId == run.FlightId);
        if (query.ReviewStatus is { } reviewStatus)
        {
            findings = reviewStatus == FindingReviewStatus.Open
                ? findings.Where(f => !reviews.Any(r => r.RuleId == f.RuleId && r.StartUs == f.StartUs && r.EndUs == f.EndUs && r.Status != FindingReviewStatus.Open))
                : findings.Where(f => reviews.Any(r => r.RuleId == f.RuleId && r.StartUs == f.StartUs && r.EndUs == f.EndUs && r.Status == reviewStatus));
        }

        if (query.AssigneeId is { } assigneeId)
        {
            findings = findings.Where(f => reviews.Any(r => r.RuleId == f.RuleId && r.StartUs == f.StartUs && r.EndUs == f.EndUs && r.AssigneeId == assigneeId));
        }

        if (query.Flagged is { } flagged)
        {
            findings = flagged
                ? findings.Where(f => reviews.Any(r => r.RuleId == f.RuleId && r.StartUs == f.StartUs && r.EndUs == f.EndUs && r.Flagged))
                : findings.Where(f => !reviews.Any(r => r.RuleId == f.RuleId && r.StartUs == f.StartUs && r.EndUs == f.EndUs && r.Flagged));
        }
        if (query.MinSeverity is { } severity)
        {
            findings = findings.Where(f => f.Severity >= severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            findings = findings.Where(f => f.Category == query.Category);
        }

        if (!string.IsNullOrWhiteSpace(query.RuleId))
        {
            findings = findings.Where(f => f.RuleId == query.RuleId);
        }

        var pageSize = Math.Clamp(query.PageSize, 1, FindingQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var total = await findings.CountAsync(cancellationToken);
        var rows = await findings.OrderBy(f => f.StartUs).ThenByDescending(f => f.Severity).ThenBy(f => f.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        var reviewed = await findingReviews.ForFindingsAsync(run.FlightId, rows, cancellationToken);
        return new PagedResult<FindingDto>([.. rows.Select(f => new FindingDto(
            f.Id, f.RunId, f.RuleId, f.RuleVersion, f.Category, f.Title, f.Description, f.Severity, f.Confidence, f.StartUs, f.EndUs, f.Signals,
            f.Measured, f.Threshold, f.ThresholdSource, f.DataCoverage, f.OccurrenceCount, f.Limitations, reviewed.GetValueOrDefault(f.Id)))], page, pageSize, total);
    }

    public IReadOnlyList<RuleDescriptorDto> ListRules() =>
        [.. engine.Rules.Select(r => new RuleDescriptorDto(r.Id, r.Version, r.Category, r.Title, r.Description, r.RequiredData, r.Applicability))];

    private async Task<AnalysisRun> LoadAuthorizedAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.AnalysisRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new NotFoundException("AnalysisRun", runId);
        await access.EnsureResourceRoleAsync(run.TeamId, TeamRole.Viewer, "AnalysisRun", runId, cancellationToken);
        return run;
    }

    internal static AnalysisRunDto ToDto(AnalysisRun r, IReadOnlyList<RuleResultDto>? rules) => new(
        r.Id, r.FlightId, r.LogFileId, r.Kind, r.Trigger, r.Status, r.EngineVersion, r.RequestedBy, r.Attempt, r.RetryCount, r.CreatedAt,
        r.NextAttemptAt, r.StartedAt, r.FinishedAt, r.DurationMs, r.FindingCount, r.InfoCount, r.WarningCount, r.CriticalCount, r.RulesApplied,
        r.RulesNotApplicable, r.RulesFailed, r.InputSummary, r.FailureCode, r.FailureMessage, r.CorrelationId, rules);
}

internal sealed class FindingQueryValidator : AbstractValidator<FindingQuery>
{
    public FindingQueryValidator()
    {
        RuleFor(x => x.MinSeverity).IsInEnum().When(x => x.MinSeverity is not null);
        RuleFor(x => x.Category).MaximumLength(50);
        RuleFor(x => x.RuleId).MaximumLength(100);
        RuleFor(x => x.ReviewStatus).IsInEnum().When(x => x.ReviewStatus is not null);
        RuleFor(x => x.PageSize).InclusiveBetween(1, FindingQuery.MaxPageSize);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
    }
}
