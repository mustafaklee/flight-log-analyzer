using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Ai;
using FlightLog.Application.Analysis;
using FlightLog.Application.Authorization;
using FlightLog.Application.Collaboration;
using FlightLog.Application.Common;
using FlightLog.Application.Comparison;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Exports;

/// <summary>A generated download.</summary>
public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

public interface IExportService
{
    /// <summary>Everything known about a flight as one JSON document: summary, analysis, findings with reviews, events, AI report, comments.</summary>
    Task<ExportFile> FlightReportJsonAsync(Guid flightId, CancellationToken cancellationToken);

    /// <summary>Findings of the flight's latest successful analysis with their review state.</summary>
    Task<ExportFile> FindingsCsvAsync(Guid flightId, CancellationToken cancellationToken);

    Task<ExportFile> EventsCsvAsync(Guid flightId, CancellationToken cancellationToken);

    Task<ExportFile> ComparisonAsync(CompareFlightsRequest request, ExportFormat format, CancellationToken cancellationToken);
}

public enum ExportFormat
{
    Json = 1,
    Csv = 2,
}

/// <summary>
/// Exports for team members (any role, like reading). Every export is written to the audit trail. Exports carry the
/// analysis version, generation time, sources and limitations; GPS positions and e-mail addresses are never exported.
/// </summary>
internal sealed class ExportService(
    IApplicationDbContext db,
    ITeamAccessService access,
    IUserDirectory users,
    IAuditTrail audit,
    IFindingReviewService reviews,
    IComparisonService comparisons,
    TimeProvider clock) : IExportService
{
    public const string FlightReportFormat = "flightlog.flight-report.v1";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly string[] FlightLimitations =
    [
        "GPS positions are not part of this export; download the original log for them.",
        "Times in µs are on the log's own clock; seconds are measured from the start of the log. UTC is given only when the log has GPS time.",
        "Findings are those of the latest successful rule analysis at the time of export; a later analysis can differ.",
        "AI report text is a model's interpretation of the analysis, not a measurement; check it against the cited findings.",
    ];

    public async Task<ExportFile> FlightReportJsonAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await LoadFlightAsync(flightId, cancellationToken);
        var now = clock.GetUtcNow();
        var project = await db.Projects.Where(p => p.Id == flight.ProjectId).Select(p => p.Name).SingleAsync(cancellationToken);
        var aircraft = flight.AircraftId is null ? null : await db.Aircraft.Where(a => a.Id == flight.AircraftId).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken);
        var file = await db.FlightLogFiles.AsNoTracking().Where(l => l.FlightId == flightId).OrderByDescending(l => l.UploadedAt).FirstOrDefaultAsync(cancellationToken);
        var run = await LatestRunAsync(flightId, cancellationToken);
        var findings = run is null ? [] : await db.AnalysisFindings.AsNoTracking().Where(f => f.RunId == run.Id).OrderBy(f => f.StartUs).ThenBy(f => f.Id).ToListAsync(cancellationToken);
        var rules = run is null ? [] : await db.AnalysisRuleResults.AsNoTracking().Where(r => r.RunId == run.Id).OrderBy(r => r.RuleId).ToListAsync(cancellationToken);
        var reviewed = await reviews.ForFindingsAsync(flightId, findings, cancellationToken);
        var events = await db.FlightEvents.AsNoTracking().Where(e => e.FlightId == flightId).OrderBy(e => e.TimestampUs).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        var changes = await db.FlightParameters.AsNoTracking().Where(p => p.FlightId == flightId && p.TimestampUs != null).OrderBy(p => p.TimestampUs).ToListAsync(cancellationToken);
        var ai = await db.AiAnalyses.AsNoTracking().Where(a => a.FlightId == flightId && a.Status == AiAnalysisStatus.Succeeded)
            .OrderByDescending(a => a.FinishedAt).ThenByDescending(a => a.Id).FirstOrDefaultAsync(cancellationToken);
        var comments = await db.FlightComments.AsNoTracking().Where(c => c.FlightId == flightId && c.DeletedAt == null).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
        var name = await UserNames.ResolveAsync(users, comments.Select(c => c.AuthorId), cancellationToken);
        var logStart = flight.LogStartTimestampUs ?? 0;

        var document = new
        {
            Format = FlightReportFormat,
            GeneratedAt = now,
            Sources = new[]
            {
                file is null ? "no log file" : $"log file {file.OriginalFileName} (sha256 {file.Sha256})",
                run is null ? "no successful rule analysis" : $"rule analysis {run.Id} ({run.EngineVersion}, finished {Csv.Cell(run.FinishedAt)})",
                ai is null ? "no AI report" : $"AI report {ai.Id} ({ai.Model}, prompt {ai.PromptVersion}, context {ai.ContextVersion} sha256 {ai.ContextHash})",
            },
            Limitations = FlightLimitations,
            Flight = new
            {
                flight.Id,
                flight.Name,
                Project = project,
                Aircraft = aircraft,
                flight.Autopilot,
                flight.FirmwareVersion,
                flight.VehicleType,
                flight.Hardware,
                flight.StartedAt,
                DurationSeconds = flight.DurationUs / 1e6,
                flight.BootTimeUtc,
                LogStartUs = flight.LogStartTimestampUs,
                flight.Tags,
                flight.Notes,
                LogFile = file is null ? null : new { file.OriginalFileName, file.SizeBytes, file.Sha256, file.Format, file.Status, file.UploadedAt },
            },
            Analysis = run is null ? null : new
            {
                run.Id,
                run.EngineVersion,
                run.FinishedAt,
                run.InputSummary,
                run.InfoCount,
                run.WarningCount,
                run.CriticalCount,
                Rules = rules.Select(r => new { r.RuleId, r.RuleVersion, r.Status, r.Reason, r.FindingCount }),
                Findings = findings.Select(f => new
                {
                    f.Id,
                    f.RuleId,
                    f.RuleVersion,
                    f.Category,
                    f.Title,
                    f.Description,
                    f.Severity,
                    f.Confidence,
                    f.StartUs,
                    f.EndUs,
                    StartSeconds = Seconds(f.StartUs, logStart),
                    EndSeconds = Seconds(f.EndUs, logStart),
                    f.Signals,
                    f.Measured,
                    f.Threshold,
                    f.ThresholdSource,
                    f.DataCoverage,
                    f.OccurrenceCount,
                    f.Limitations,
                    Review = reviewed.GetValueOrDefault(f.Id) is { } r
                        ? new { r.Status, Assignee = r.Assignee?.DisplayName, r.Flagged, r.UpdatedAt, UpdatedBy = r.UpdatedBy.DisplayName, r.CommentCount }
                        : null,
                }),
            },
            Events = events.Select(e => new { e.TimestampUs, Seconds = Seconds(e.TimestampUs, logStart), e.Kind, e.Severity, e.Source, e.Text }),
            ParameterChanges = changes.Select(p => new { p.TimestampUs, Seconds = Seconds(p.TimestampUs!.Value, logStart), p.Name, p.Value }),
            AiReport = ai is null ? null : new
            {
                ai.Id,
                ai.Model,
                ai.PromptVersion,
                ai.ContextVersion,
                ai.ContextHash,
                ai.FinishedAt,
                ai.AnalysisRunId,
                Report = ai.Report is null ? null : JsonSerializer.Deserialize<AiReportDto>(ai.Report, FlightReportValidator.StorageOptions),
            },
            Comments = comments.Select(c => new
            {
                Author = name(c.AuthorId).DisplayName,
                c.Body,
                c.StartUs,
                c.EndUs,
                StartSeconds = c.StartUs is { } s ? Seconds(s, logStart) : (double?)null,
                EndSeconds = c.EndUs is { } e ? Seconds(e, logStart) : (double?)null,
                c.CreatedAt,
                c.EditedAt,
                OnFinding = c.FindingReviewId is not null,
            }),
        };

        var content = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        return await AuditedAsync(flight, "flight-report", "json", new ExportFile(FileName(flight.Name, "rapor", now, "json"), "application/json", content), cancellationToken);
    }

    public async Task<ExportFile> FindingsCsvAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await LoadFlightAsync(flightId, cancellationToken);
        var now = clock.GetUtcNow();
        var run = await LatestRunAsync(flightId, cancellationToken);
        var findings = run is null ? [] : await db.AnalysisFindings.AsNoTracking().Where(f => f.RunId == run.Id).OrderBy(f => f.StartUs).ThenBy(f => f.Id).ToListAsync(cancellationToken);
        var reviewed = await reviews.ForFindingsAsync(flightId, findings, cancellationToken);
        var logStart = flight.LogStartTimestampUs ?? 0;

        var csv = Header(new Csv(), "findings", flight, now)
            .Meta("analysis", run is null ? "none (the flight has no successful rule analysis)" : $"{run.Id} {run.EngineVersion} finished {Csv.Cell(run.FinishedAt)}")
            .Meta("limitations", "Findings of the latest successful analysis; times on the log clock (µs) and in seconds from log start.")
            .Row("finding_id", "rule_id", "rule_version", "category", "severity", "confidence", "title", "description", "start_s", "end_s", "start_us", "end_us",
                "start_utc", "measured", "threshold", "threshold_source", "data_coverage", "occurrences", "limitations",
                "review_status", "assignee", "flagged", "comments");
        foreach (var f in findings)
        {
            var r = reviewed.GetValueOrDefault(f.Id);
            csv.Row(f.Id, f.RuleId, f.RuleVersion, f.Category, f.Severity, f.Confidence, f.Title, f.Description,
                Seconds(f.StartUs, logStart), Seconds(f.EndUs, logStart), f.StartUs, f.EndUs, Utc(flight, f.StartUs),
                Pairs(f.Measured), Pairs(f.Threshold), f.ThresholdSource, f.DataCoverage, f.OccurrenceCount, f.Limitations,
                r?.Status.ToString() ?? "Open", r?.Assignee?.DisplayName, r?.Flagged ?? false, r?.CommentCount ?? 0);
        }

        return await AuditedAsync(flight, "findings", "csv", new ExportFile(FileName(flight.Name, "bulgular", now, "csv"), "text/csv", csv.ToBytes()), cancellationToken);
    }

    public async Task<ExportFile> EventsCsvAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await LoadFlightAsync(flightId, cancellationToken);
        var now = clock.GetUtcNow();
        var events = await db.FlightEvents.AsNoTracking().Where(e => e.FlightId == flightId).OrderBy(e => e.TimestampUs).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        var logStart = flight.LogStartTimestampUs ?? 0;

        var csv = Header(new Csv(), "events", flight, now)
            .Meta("limitations", "Event texts are copied from the log as written by the autopilot.")
            .Row("time_s", "timestamp_us", "utc", "kind", "severity", "source", "text");
        foreach (var e in events)
        {
            csv.Row(Seconds(e.TimestampUs, logStart), e.TimestampUs, Utc(flight, e.TimestampUs), e.Kind, e.Severity, e.Source, e.Text);
        }

        return await AuditedAsync(flight, "events", "csv", new ExportFile(FileName(flight.Name, "olaylar", now, "csv"), "text/csv", csv.ToBytes()), cancellationToken);
    }

    public async Task<ExportFile> ComparisonAsync(CompareFlightsRequest request, ExportFormat format, CancellationToken cancellationToken)
    {
        var comparison = await comparisons.CompareAsync(request, cancellationToken);
        var stamp = comparison.GeneratedAt.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var file = format == ExportFormat.Json
            ? new ExportFile($"karsilastirma-{stamp}.json", "application/json", JsonSerializer.SerializeToUtf8Bytes(comparison, JsonOptions))
            : new ExportFile($"karsilastirma-{stamp}.csv", "text/csv", ComparisonCsv(comparison).ToBytes());

        var teams = await db.Flights.Where(f => request.FlightIds.Contains(f.Id)).Select(f => new { f.Id, f.TeamId }).ToListAsync(cancellationToken);
        foreach (var flight in teams)
        {
            audit.Record(flight.TeamId, AuditActions.DataExported, "Comparison", flight.Id, flight.Id, $"comparison ({format})",
                new Dictionary<string, string?> { ["kind"] = "comparison", ["format"] = format.ToString().ToLowerInvariant(), ["flights"] = string.Join(",", request.FlightIds) });
        }

        await db.SaveChangesAsync(cancellationToken);
        return file;
    }

    internal static Csv ComparisonCsv(FlightComparisonDto c)
    {
        var csv = new Csv()
            .Meta("export", "flight comparison")
            .Meta("version", c.Version)
            .Meta("generated_at", Csv.Cell(c.GeneratedAt));
        foreach (var note in c.Notes)
        {
            csv.Meta($"note {note.Code}", note.Message + (note.FlightIds.Count > 0 ? $" (flights: {string.Join(", ", note.FlightIds)})" : ""));
        }

        foreach (var limitation in c.Limitations)
        {
            csv.Meta("limitation", limitation);
        }

        csv.Row(["section", "metric", "unit", .. c.Flights.Select(f => (object?)f.Name)]);
        void Row(string section, string metric, string? unit, IEnumerable<object?> values) => csv.Row([section, metric, unit, .. values]);

        Row("flight", "id", null, c.Flights.Select(f => (object?)f.Id));
        Row("flight", "autopilot", null, c.Flights.Select(f => (object?)f.Autopilot));
        Row("flight", "firmware", null, c.Flights.Select(f => (object?)f.FirmwareVersion));
        Row("flight", "vehicle_type", null, c.Flights.Select(f => (object?)f.VehicleType));
        Row("flight", "aircraft", null, c.Flights.Select(f => (object?)f.AircraftName));
        Row("flight", "started_at", null, c.Flights.Select(f => (object?)f.StartedAt));
        Row("flight", "duration", "s", c.Flights.Select(f => (object?)f.DurationSeconds));
        Row("flight", "analyzed_time", "s", c.Flights.Select(f => (object?)f.AnalyzedSeconds));
        Row("flight", "arming_known", null, c.Flights.Select(f => (object?)f.ArmingKnown));
        Row("analysis", "engine_version", null, c.Flights.Select(f => (object?)f.EngineVersion));
        Row("analysis", "findings_info", null, c.Flights.Select(f => (object?)f.InfoCount));
        Row("analysis", "findings_warning", null, c.Flights.Select(f => (object?)f.WarningCount));
        Row("analysis", "findings_critical", null, c.Flights.Select(f => (object?)f.CriticalCount));
        foreach (var category in c.Flights.SelectMany(f => f.FindingsByCategory.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Row("findings_by_category", category, null, c.Flights.Select(f => (object?)f.FindingsByCategory.GetValueOrDefault(category)));
        }

        foreach (var rule in c.Rules)
        {
            Row("rule", rule.RuleId, null, rule.Values.Select(v => (object?)(v is null ? null : $"{v.Status}: {v.Findings} ({v.HighestSeverity?.ToString() ?? "-"})")));
        }

        foreach (var kind in c.Flights.SelectMany(f => f.EventCounts.Keys).Distinct(StringComparer.Ordinal))
        {
            Row("events", kind, null, c.Flights.Select(f => (object?)f.EventCounts.GetValueOrDefault(kind)));
        }

        foreach (var signal in c.Signals)
        {
            foreach (var (stat, pick) in StatColumns)
            {
                Row("signal", $"{signal.Name}:{stat}", stat is "samples" ? null : stat is "rate" ? "Hz" : signal.Unit, signal.Values.Select(v => v is null ? null : (object?)pick(v)));
            }
        }

        Row("battery", "start_voltage", "V", c.Flights.Select(f => (object?)f.Battery?.StartVoltage));
        Row("battery", "end_voltage", "V", c.Flights.Select(f => (object?)f.Battery?.EndVoltage));
        Row("battery", "min_voltage", "V", c.Flights.Select(f => (object?)f.Battery?.MinVoltage));
        Row("battery", "mean_current", "A", c.Flights.Select(f => (object?)f.Battery?.MeanCurrent));
        Row("battery", "max_current", "A", c.Flights.Select(f => (object?)f.Battery?.MaxCurrent));
        Row("battery", "used", "%", c.Flights.Select(f => (object?)f.Battery?.UsedPercent));
        Row("vibration", "metric", null, c.Flights.Select(f => (object?)f.Vibration?.Metric));
        Row("vibration", "mean", null, c.Flights.Select(f => (object?)f.Vibration?.Mean));
        Row("vibration", "p95", null, c.Flights.Select(f => (object?)f.Vibration?.P95));
        Row("vibration", "max", null, c.Flights.Select(f => (object?)f.Vibration?.Max));
        foreach (var mode in c.Flights.SelectMany(f => f.Modes.Select(m => m.Mode)).Distinct(StringComparer.Ordinal))
        {
            Row("mode_time", mode, "s", c.Flights.Select(f => (object?)f.Modes.FirstOrDefault(m => m.Mode == mode)?.Seconds));
        }

        Row("ai_report", "assessment", null, c.Flights.Select(f => (object?)f.LatestAiReport?.Assessment));
        return csv;
    }

    private static readonly (string Name, Func<SignalStatsDto, object> Pick)[] StatColumns =
    [
        ("samples", s => s.Samples), ("rate", s => s.RateHz), ("min", s => s.Min), ("p5", s => s.P5), ("p50", s => s.P50),
        ("mean", s => s.Mean), ("p95", s => s.P95), ("max", s => s.Max),
    ];

    private async Task<Domain.Flights.Flight> LoadFlightAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await db.Flights.AsNoTracking().SingleOrDefaultAsync(f => f.Id == flightId, cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(flight.TeamId, TeamRole.Viewer, "Flight", flightId, cancellationToken);
        return flight;
    }

    private Task<AnalysisRun?> LatestRunAsync(Guid flightId, CancellationToken cancellationToken) =>
        db.AnalysisRuns.AsNoTracking().Where(r => r.FlightId == flightId && r.Status == AnalysisRunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);

    private async Task<ExportFile> AuditedAsync(Domain.Flights.Flight flight, string kind, string format, ExportFile file, CancellationToken cancellationToken)
    {
        audit.Record(flight.TeamId, AuditActions.DataExported, AuditEntities.Flight, flight.Id, flight.Id, $"{flight.Name}: {kind} ({format})",
            new Dictionary<string, string?> { ["kind"] = kind, ["format"] = format, ["bytes"] = file.Content.Length.ToString(CultureInfo.InvariantCulture) });
        await db.SaveChangesAsync(cancellationToken);
        return file;
    }

    private static Csv Header(Csv csv, string kind, Domain.Flights.Flight flight, DateTimeOffset now) => csv
        .Meta("export", kind)
        .Meta("flight", $"{flight.Name} ({flight.Id})")
        .Meta("autopilot", $"{flight.Autopilot} {flight.FirmwareVersion}")
        .Meta("generated_at", Csv.Cell(now));

    private static double Seconds(long us, long logStart) => Math.Round((us - logStart) / 1e6, 3);

    private static DateTimeOffset? Utc(Domain.Flights.Flight flight, long us) => flight.BootTimeUtc?.AddTicks(us * 10);

    private static string Pairs(IReadOnlyDictionary<string, double> values) =>
        string.Join("; ", values.Select(p => $"{p.Key}={p.Value.ToString("R", CultureInfo.InvariantCulture)}"));

    private static string FileName(string flightName, string kind, DateTimeOffset now, string extension)
    {
        var safe = new string([.. flightName.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')]).Trim('-');
        if (safe.Length > 40)
        {
            safe = safe[..40];
        }

        return $"{(safe.Length == 0 ? "ucus" : safe)}-{kind}-{now.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.{extension}";
    }
}
