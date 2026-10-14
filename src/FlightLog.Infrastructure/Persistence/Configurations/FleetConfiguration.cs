using FlightLog.Domain.Fleet;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightLog.Infrastructure.Persistence.Configurations;

internal sealed class AircraftConfiguration : IEntityTypeConfiguration<Aircraft>
{
    public void Configure(EntityTypeBuilder<Aircraft> builder)
    {
        builder.ToTable("Aircraft");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Name).HasMaxLength(Aircraft.NameMaxLength).IsRequired();
        builder.Property(a => a.Identifier).HasMaxLength(Aircraft.IdentifierMaxLength);
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.Autopilot).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.FirmwareName).HasMaxLength(Aircraft.TextMaxLength);
        builder.Property(a => a.FirmwareVersion).HasMaxLength(Aircraft.TextMaxLength);
        builder.Property(a => a.FlightController).HasMaxLength(Aircraft.TextMaxLength);
        builder.Property(a => a.Hardware).HasMaxLength(Aircraft.TextMaxLength);
        builder.Property(a => a.Description).HasMaxLength(Aircraft.DescriptionMaxLength);
        builder.PrimitiveCollection(a => a.Tags).HasField("_tags").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(a => a.Version).IsRowVersion();

        builder.HasIndex(a => new { a.TeamId, a.Name });
        builder.HasIndex(a => new { a.TeamId, a.Identifier }).IsUnique().HasFilter("\"Identifier\" IS NOT NULL");

        builder.HasOne<Team>().WithMany().HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}
