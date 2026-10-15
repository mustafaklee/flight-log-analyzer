using FlightLog.Application.Messaging;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightLog.Infrastructure.Persistence.Configurations;

internal sealed class AnalysisRunConfiguration : IEntityTypeConfiguration<AnalysisRun>
{
    public void Configure(EntityTypeBuilder<AnalysisRun> builder)
    {
        builder.ToTable("AnalysisRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.EngineVersion).HasMaxLength(50).IsRequired();
        builder.Property(r => r.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(r => r.WorkerId).HasMaxLength(200);
        builder.Property(r => r.FailureCode).HasMaxLength(100);
        builder.Property(r => r.FailureMessage).HasMaxLength(AnalysisRun.FailureMessageMaxLength);
        builder.Property(r => r.InputSummary).AsRequiredJsonb().HasDefaultValueSql("'{}'::jsonb");
        builder.Property(r => r.Version).IsRowVersion();
        builder.Ignore(r => r.DomainEvents);

        builder.HasIndex(r => new { r.FlightId, r.CreatedAt });
        builder.HasIndex(r => new { r.Status, r.CreatedAt });
        builder.HasIndex(r => r.LogFileId);
        builder.HasOne<Flight>().WithMany().HasForeignKey(r => r.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FlightLogFile>().WithMany().HasForeignKey(r => r.LogFileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Team>().WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AnalysisFindingConfiguration : IEntityTypeConfiguration<AnalysisFinding>
{
    public void Configure(EntityTypeBuilder<AnalysisFinding> builder)
    {
        builder.ToTable("AnalysisFindings");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.RuleId).HasMaxLength(100).IsRequired();
        builder.Property(f => f.Category).HasMaxLength(50).IsRequired();
        builder.Property(f => f.Title).HasMaxLength(AnalysisFinding.TitleMaxLength).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(AnalysisFinding.DescriptionMaxLength).IsRequired();
        builder.Property(f => f.Severity).HasConversion<string>().HasMaxLength(32);
        builder.Property(f => f.Confidence).HasConversion<string>().HasMaxLength(32);
        builder.Property(f => f.Signals).AsJsonb<IReadOnlyList<string>, List<string>>();
        builder.Property(f => f.Measured).AsJsonb<IReadOnlyDictionary<string, double>, Dictionary<string, double>>();
        builder.Property(f => f.Threshold).AsJsonb<IReadOnlyDictionary<string, double>, Dictionary<string, double>>();
        builder.Property(f => f.ThresholdSource).HasMaxLength(500).IsRequired();
        builder.Property(f => f.Limitations).HasMaxLength(1000);

        builder.HasIndex(f => new { f.RunId, f.StartUs });
        builder.HasIndex(f => f.FlightId);
        builder.HasOne<AnalysisRun>().WithMany().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Flight>().WithMany().HasForeignKey(f => f.FlightId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisRuleResultConfiguration : IEntityTypeConfiguration<AnalysisRuleResult>
{
    public void Configure(EntityTypeBuilder<AnalysisRuleResult> builder)
    {
        builder.ToTable("AnalysisRuleResults");
        builder.HasKey(r => new { r.RunId, r.RuleId });
        builder.Property(r => r.RuleId).HasMaxLength(100);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Reason).HasMaxLength(1000);
        builder.Property(r => r.Signals).AsJsonb<IReadOnlyList<string>, List<string>>();
        builder.HasOne<AnalysisRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.ErrorMaxLength);

        // The publisher scans unpublished rows in order of availability.
        builder.HasIndex(m => new { m.AvailableAt, m.Id }).HasFilter("\"PublishedAt\" IS NULL");
        builder.HasIndex(m => m.AggregateId);
        builder.HasIndex(m => m.PublishedAt);
    }
}

internal sealed class AiAnalysisConfiguration : IEntityTypeConfiguration<AiAnalysis>
{
    public void Configure(EntityTypeBuilder<AiAnalysis> builder)
    {
        builder.ToTable("AiAnalyses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.Provider).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Model).HasMaxLength(100).IsRequired();
        builder.Property(a => a.ResponseModel).HasMaxLength(100);
        builder.Property(a => a.PromptVersion).HasMaxLength(50).IsRequired();
        builder.Property(a => a.ContextVersion).HasMaxLength(50);
        builder.Property(a => a.ContextHash).HasMaxLength(64);
        // Text, not jsonb: the stored context must stay byte-identical to what was hashed and sent.
        builder.Property(a => a.Context).HasColumnType("text");
        builder.Property(a => a.ContextSummary).AsRequiredJsonb().HasDefaultValueSql("'{}'::jsonb");
        builder.Property(a => a.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(a => a.WorkerId).HasMaxLength(200);
        builder.Property(a => a.CostUsd).HasPrecision(18, 6);
        builder.Property(a => a.StopReason).HasMaxLength(50);
        builder.Property(a => a.RawOutput).HasMaxLength(AiAnalysis.RawOutputMaxLength);
        builder.Property(a => a.Report).HasColumnType("jsonb");
        builder.Property(a => a.FailureCode).HasMaxLength(100);
        builder.Property(a => a.FailureMessage).HasMaxLength(AiAnalysis.FailureMessageMaxLength);
        builder.Property(a => a.Version).IsRowVersion();
        builder.Ignore(a => a.DomainEvents);

        builder.HasIndex(a => new { a.FlightId, a.CreatedAt });
        builder.HasIndex(a => new { a.Status, a.CreatedAt });
        builder.HasIndex(a => new { a.TeamId, a.CreatedAt });
        builder.HasIndex(a => a.AnalysisRunId);
        builder.HasOne<Flight>().WithMany().HasForeignKey(a => a.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AnalysisRun>().WithMany().HasForeignKey(a => a.AnalysisRunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Team>().WithMany().HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}
