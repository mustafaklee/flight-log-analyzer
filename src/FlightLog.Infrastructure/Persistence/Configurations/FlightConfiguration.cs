using FlightLog.Domain.Fleet;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Projects;
using FlightLog.Domain.Teams;
using FlightLog.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightLog.Infrastructure.Persistence.Configurations;

internal sealed class FlightConfiguration : IEntityTypeConfiguration<Flight>
{
    public void Configure(EntityTypeBuilder<Flight> builder)
    {
        builder.ToTable("Flights");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Name).HasMaxLength(Flight.NameMaxLength).IsRequired();
        builder.Property(f => f.Notes).HasMaxLength(Flight.NotesMaxLength);
        builder.Property(f => f.Autopilot).HasConversion<string>().HasMaxLength(32);
        builder.Property(f => f.FirmwareVersion).HasMaxLength(200);
        builder.Property(f => f.Version).IsRowVersion();

        // Flight history queries: by team or project, newest first; by aircraft.
        builder.HasIndex(f => new { f.TeamId, f.OccurredAt });
        builder.HasIndex(f => new { f.ProjectId, f.OccurredAt });
        builder.HasIndex(f => new { f.AircraftId, f.OccurredAt });

        builder.HasOne<Team>().WithMany().HasForeignKey(f => f.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Aircraft>().WithMany().HasForeignKey(f => f.AircraftId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(f => f.CreatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FlightLogFileConfiguration : IEntityTypeConfiguration<FlightLogFile>
{
    public void Configure(EntityTypeBuilder<FlightLogFile> builder)
    {
        builder.ToTable("FlightLogFiles");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.OriginalFileName).HasMaxLength(FlightLogFile.FileNameMaxLength).IsRequired();
        builder.Property(l => l.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(l => l.Format).HasConversion<string>().HasMaxLength(32);
        builder.Property(l => l.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(l => l.FailureCode).HasMaxLength(100);
        builder.Property(l => l.FailureMessage).HasMaxLength(FlightLogFile.FailureMessageMaxLength);
        builder.Property(l => l.Version).IsRowVersion();

        // Duplicate detection is per team: the same content may belong to several teams.
        builder.HasIndex(l => new { l.TeamId, l.Sha256 }).IsUnique();
        builder.HasIndex(l => l.FlightId);
        builder.HasIndex(l => l.Status);
        builder.HasIndex(l => l.StorageKey);

        builder.HasOne<Flight>().WithMany().HasForeignKey(l => l.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Team>().WithMany().HasForeignKey(l => l.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.UploadedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
