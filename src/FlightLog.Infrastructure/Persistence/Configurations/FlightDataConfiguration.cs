using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightLog.Infrastructure.Persistence.Configurations;

internal sealed class LogProcessingJobConfiguration : IEntityTypeConfiguration<LogProcessingJob>
{
    public void Configure(EntityTypeBuilder<LogProcessingJob> builder)
    {
        builder.ToTable("LogProcessingJobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.WorkerId).HasMaxLength(200).IsRequired();
        builder.Property(j => j.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(j => j.StoragePrefix).HasMaxLength(200).IsRequired();
        builder.Property(j => j.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(j => j.ParserName).HasMaxLength(50);
        builder.Property(j => j.ParserVersion).HasMaxLength(50);
        builder.Property(j => j.NormalizationVersion).HasMaxLength(50);
        builder.Property(j => j.FailureCode).HasMaxLength(100);
        builder.Property(j => j.FailureMessage).HasMaxLength(LogProcessingJob.FailureMessageMaxLength);

        builder.HasIndex(j => new { j.LogFileId, j.StartedAt });
        builder.HasOne<FlightLogFile>().WithMany().HasForeignKey(j => j.LogFileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Flight>().WithMany().HasForeignKey(j => j.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Team>().WithMany().HasForeignKey(j => j.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FlightSignalSeriesConfiguration : IEntityTypeConfiguration<FlightSignalSeries>
{
    public void Configure(EntityTypeBuilder<FlightSignalSeries> builder)
    {
        builder.ToTable("FlightSignalSeries");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Message).HasMaxLength(FlightSignal.NameMaxLength).IsRequired();
        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.StoragePrefix).HasMaxLength(300).IsRequired();
        builder.HasIndex(s => s.FlightId);
        builder.HasOne<Flight>().WithMany().HasForeignKey(s => s.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LogProcessingJob>().WithMany().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SignalChunkInfoConfiguration : IEntityTypeConfiguration<SignalChunkInfo>
{
    public void Configure(EntityTypeBuilder<SignalChunkInfo> builder)
    {
        builder.ToTable("SignalChunks");
        builder.HasKey(c => new { c.SeriesId, c.Index });
        builder.HasOne<FlightSignalSeries>().WithMany().HasForeignKey(c => c.SeriesId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FlightSignalConfiguration : IEntityTypeConfiguration<FlightSignal>
{
    public void Configure(EntityTypeBuilder<FlightSignal> builder)
    {
        builder.ToTable("FlightSignals");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(FlightSignal.NameMaxLength).IsRequired();
        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.Message).HasMaxLength(FlightSignal.NameMaxLength).IsRequired();
        builder.Property(s => s.Field).HasMaxLength(FlightSignal.NameMaxLength).IsRequired();
        builder.Property(s => s.SourceType).HasMaxLength(50).IsRequired();
        builder.Property(s => s.Unit).HasMaxLength(50);
        builder.Property(s => s.SourceMessage).HasMaxLength(FlightSignal.NameMaxLength);
        builder.Property(s => s.SourceFields).HasMaxLength(FlightSignal.NameMaxLength);
        builder.Property(s => s.Conversion).HasMaxLength(300);
        builder.Property(s => s.NormalizationVersion).HasMaxLength(50);
        builder.HasIndex(s => new { s.FlightId, s.Name }).IsUnique();
        builder.HasOne<Flight>().WithMany().HasForeignKey(s => s.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FlightSignalSeries>().WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FlightEventConfiguration : IEntityTypeConfiguration<FlightEvent>
{
    public void Configure(EntityTypeBuilder<FlightEvent> builder)
    {
        builder.ToTable("FlightEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Severity).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Source).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Text).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Data).AsJsonb();
        builder.HasIndex(e => new { e.FlightId, e.TimestampUs });
        builder.HasOne<Flight>().WithMany().HasForeignKey(e => e.FlightId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FlightParameterConfiguration : IEntityTypeConfiguration<FlightParameter>
{
    public void Configure(EntityTypeBuilder<FlightParameter> builder)
    {
        builder.ToTable("FlightParameters");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(p => new { p.FlightId, p.Name });
        builder.HasOne<Flight>().WithMany().HasForeignKey(p => p.FlightId).OnDelete(DeleteBehavior.Cascade);
    }
}
