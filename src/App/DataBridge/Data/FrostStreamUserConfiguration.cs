using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class FrostStreamUserConfiguration : IEntityTypeConfiguration<FrostStreamUserEntity>
{
    public void Configure(EntityTypeBuilder<FrostStreamUserEntity> builder)
    {
        builder.ToTable("froststream_users", "auth");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.AuthentikSubjectId).HasColumnName("authentik_subject_id").HasMaxLength(255).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.Preferences).HasColumnName("preferences").HasColumnType("jsonb");
        // The app always sets CreatedAt from the injected clock on insert, so EF persists that value
        // verbatim and echoes it back (no store-generated readback dance). The DB column keeps its own
        // CURRENT_TIMESTAMP default (FluentMigrator) as a safety net for any non-EF inserts.
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamp with time zone");

        builder.HasIndex(x => x.AuthentikSubjectId)
            .HasDatabaseName("ux_froststream_users_authentik_subject_id")
            .IsUnique();
    }
}
