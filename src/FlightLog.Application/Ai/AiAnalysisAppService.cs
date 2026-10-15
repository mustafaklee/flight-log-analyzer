using System.Globalization;
using System.Text.Json;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Collaboration;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Ai;

public sealed record AiAnalysisDto(
    Guid Id,
    Guid FlightId,
    Guid AnalysisRunId,
    AiAnalysisStatus Status,
    string Provider,
    string Model,
    string? ResponseModel,
    string PromptVersion,
    string? ContextVersion,
    string? ContextHash,
    IReadOnlyDictionary<string, string> ContextSummary,
    long? LogStartUs,
    Guid RequestedBy,
    int Attempt,
    int RetryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    int ProviderCalls,
    long LatencyMs,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal? CostUsd,
    string? StopReason,
    string? FailureCode,
    string? FailureMessage,
    string CorrelationId,
    AiReportDto? Report);

/// <summary>Whether a team can request AI reports, and how much of today's quota is used.</summary>
public sealed record AiStatusDto(bool Enabled, string Provider, string Model, string PromptVersion, int DailyLimit, int UsedToday);

/// <summary>AI analyses requested from the API (the worker writes the reports, see <see cref="IAiAnalysisService"/>).</summary>
public interface IAiAnalysisAppService
{
    /// <summary>Queues an AI report of the flight's latest successful rule analysis (Engineer).</summary>
    Task<AiAnalysisDto> RequestAsync(Guid flightId, CancellationToken cancellationToken);

    /// <summary>AI analyses of a flight, newest first, without the report body.</summary>
    Task<IReadOnlyList<AiAnalysisDto>> ListForFlightAsync(Guid flightId, CancellationToken cancellationToken);

    Task<AiAnalysisDto> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The exact context sent to the model (JSON), for review (Engineer).</summary>
    Task<string> GetContextAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The report as Markdown, with a file name.</summary>
    Task<(string FileName, string Markdown)> GetMarkdownAsync(Guid id, CancellationToken cancellationToken);

    Task<AiStatusDto> GetStatusAsync(Guid teamId, CancellationToken cancellationToken);
}

internal sealed class AiAnalysisAppService(
    IApplicationDbContext db,
    ITeamAccessService access,
    ICurrentUser currentUser,
    IAuditTrail audit,
    TimeProvider clock,
    IOptions<AiOptions> options) : IAiAnalysisAppService
{
    public async Task<AiAnalysisDto> RequestAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await db.Flights.AsNoTracking().SingleOrDefaultAsync(f => f.Id == flightId, cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(flight.TeamId, TeamRole.Engineer, "Flight", flightId, cancellationToken);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            throw new DomainRuleViolationException("ai.disabled", "AI analysis is not enabled on this server.");
        }

        if (await db.AiAnalyses.AnyAsync(a => a.FlightId == flightId && (a.Status == AiAnalysisStatus.Queued || a.Status == AiAnalysisStatus.Running), cancellationToken))
        {
            throw new ConflictException("ai.already_pending", "An AI analysis of this flight is already queued or running.");
        }

        var latestRun = await db.AnalysisRuns.AsNoTracking().Where(r => r.FlightId == flightId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);
        if (latestRun is { Status: AnalysisRunStatus.Queued or AnalysisRunStatus.Running })
        {
            throw new ConflictException("ai.rule_analysis_pending", "The rule analysis of this flight is still running. Request the AI report when it is done.");
        }

        var basis = await db.AnalysisRuns.AsNoTracking().Where(r => r.FlightId == flightId && r.Status == AnalysisRunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainRuleViolationException("ai.no_rule_analysis", "The flight has no successful rule analysis to base an AI report on.");

        var now = clock.GetUtcNow();
        if (settings.DailyLimitPerTeam > 0)
        {
            var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var used = await db.AiAnalyses.CountAsync(a => a.TeamId == flight.TeamId && a.CreatedAt >= dayStart, cancellationToken);
            if (used >= settings.DailyLimitPerTeam)
            {
                throw new QuotaExceededException(
                    "ai.quota_exceeded",
                    $"The team has used its {settings.DailyLimitPerTeam} AI analyses for today (UTC).",
                    dayStart.AddDays(1) - now);
            }
        }

        var analysis = AiAnalysis.Queue(flight.TeamId, flightId, basis.Id, currentUser.UserId, settings.Provider, settings.Model, FlightReportPrompt.Version, now);
        db.AiAnalyses.Add(analysis);
        audit.Record(flight.TeamId, AuditActions.AiAnalysisRequested, AuditEntities.AiAnalysis, analysis.Id, flightId, $"{flight.Name} ({settings.Model})");
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(analysis, includeReport: false);
    }

    public async Task<IReadOnlyList<AiAnalysisDto>> ListForFlightAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var teamId = await db.Flights.AsNoTracking().Where(f => f.Id == flightId).Select(f => (Guid?)f.TeamId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(teamId, TeamRole.Viewer, "Flight", flightId, cancellationToken);

        var rows = await db.AiAnalyses.AsNoTracking().Where(a => a.FlightId == flightId).OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        return [.. rows.Select(a => ToDto(a, includeReport: false))];
    }

    public async Task<AiAnalysisDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await LoadAuthorizedAsync(id, TeamRole.Viewer, cancellationToken), includeReport: true);

    public async Task<string> GetContextAsync(Guid id, CancellationToken cancellationToken)
    {
        var analysis = await LoadAuthorizedAsync(id, TeamRole.Engineer, cancellationToken);
        return analysis.Context ?? throw new DomainRuleViolationException("ai.no_context", "The analysis has not prepared its context yet.");
    }

    public async Task<(string FileName, string Markdown)> GetMarkdownAsync(Guid id, CancellationToken cancellationToken)
    {
        var analysis = await LoadAuthorizedAsync(id, TeamRole.Viewer, cancellationToken);
        if (analysis.Status != AiAnalysisStatus.Succeeded)
        {
            throw new DomainRuleViolationException("ai.no_report", "The analysis has no report.");
        }

        var flightName = await db.Flights.AsNoTracking().Where(f => f.Id == analysis.FlightId).Select(f => f.Name).SingleAsync(cancellationToken);
        var markdown = FlightReportMarkdown.Render(ToDto(analysis, includeReport: true), flightName);
        var fileName = $"ai-rapor-{analysis.FinishedAt!.Value.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}-{analysis.Id.ToString()[..8]}.md";
        return (fileName, markdown);
    }

    public async Task<AiStatusDto> GetStatusAsync(Guid teamId, CancellationToken cancellationToken)
    {
        await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);
        var settings = options.Value;
        var dayStart = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var used = await db.AiAnalyses.CountAsync(a => a.TeamId == teamId && a.CreatedAt >= dayStart, cancellationToken);
        return new AiStatusDto(settings.Enabled, settings.Provider, settings.Model, FlightReportPrompt.Version, settings.DailyLimitPerTeam, used);
    }

    private async Task<AiAnalysis> LoadAuthorizedAsync(Guid id, TeamRole role, CancellationToken cancellationToken)
    {
        var analysis = await db.AiAnalyses.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new NotFoundException("AiAnalysis", id);
        await access.EnsureResourceRoleAsync(analysis.TeamId, role, "AiAnalysis", id, cancellationToken);
        return analysis;
    }

    internal static AiAnalysisDto ToDto(AiAnalysis a, bool includeReport) => new(
        a.Id, a.FlightId, a.AnalysisRunId, a.Status, a.Provider, a.Model, a.ResponseModel, a.PromptVersion, a.ContextVersion, a.ContextHash,
        a.ContextSummary,
        a.ContextSummary.TryGetValue("logStartUs", out var start) && long.TryParse(start, CultureInfo.InvariantCulture, out var us) ? us : null,
        a.RequestedBy, a.Attempt, a.RetryCount, a.CreatedAt, a.NextAttemptAt, a.StartedAt, a.FinishedAt, a.DurationMs, a.ProviderCalls, a.LatencyMs,
        a.InputTokens, a.OutputTokens, a.CacheReadTokens, a.CacheWriteTokens, a.CostUsd, a.StopReason, a.FailureCode, a.FailureMessage, a.CorrelationId,
        includeReport && a.Report is { } report ? JsonSerializer.Deserialize<AiReportDto>(report, FlightReportValidator.StorageOptions) : null);
}
