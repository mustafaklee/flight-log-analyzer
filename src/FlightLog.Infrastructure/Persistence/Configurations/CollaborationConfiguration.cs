using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using FlightLog.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightLog.Infrastructure.Persistence.Configurations;

internal sealed class FindingReviewConfiguration : IEntityTypeConfiguration<FindingReview>
{
    public void Configure(EntityTypeBuilder<FindingReview> builder)
    {
        builder.ToTable("FindingReviews");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.RuleId).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Version).IsRowVersion();

        // One review per finding identity (flight, rule, time range); findings themselves are rewritten by re-analysis.
        builder.HasIndex(r => new { r.FlightId, r.RuleId, r.StartUs, r.EndUs }).IsUnique();

        // Team work lists: assigned to someone, by status, most recent first.
        builder.HasIndex(r => new { r.TeamId, r.AssigneeId, r.UpdatedAt });
        builder.HasIndex(r => new { r.TeamId, r.Status, r.UpdatedAt });

        builder.HasOne<Flight>().WithMany().HasForeignKey(r => r.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Team>().WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.AssigneeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FlightCommentConfiguration : IEntityTypeConfiguration<FlightComment>
{
    public void Configure(EntityTypeBuilder<FlightComment> builder)
    {
        builder.ToTable("FlightComments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Body).HasMaxLength(FlightComment.BodyMaxLength);
        builder.Property(c => c.Version).IsRowVersion();
        builder.Ignore(c => c.IsDeleted);

        builder.HasIndex(c => new { c.FlightId, c.CreatedAt });
        builder.HasIndex(c => c.FindingReviewId);

        builder.HasOne<Flight>().WithMany().HasForeignKey(c => c.FlightId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FindingReview>().WithMany().HasForeignKey(c => c.FindingReviewId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Team>().WithMany().HasForeignKey(c => c.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Action).HasMaxLength(AuditEntry.ActionMaxLength).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(AuditEntry.SummaryMaxLength);
        builder.Property(a => a.Data).AsJsonb<IReadOnlyDictionary<string, string?>, Dictionary<string, string?>>();
        builder.Property(a => a.CorrelationId).HasMaxLength(100).IsRequired();

        // No foreign keys on purpose: the trail must outlive the flights, comments and memberships it describes.
        builder.HasIndex(a => new { a.TeamId, a.CreatedAt });
        builder.HasIndex(a => new { a.FlightId, a.CreatedAt });
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
        builder.HasIndex(a => a.CreatedAt);
    }
}
