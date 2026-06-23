using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class CookieProfileConfiguration : IEntityTypeConfiguration<CookieProfileEntity>
{
    public void Configure(EntityTypeBuilder<CookieProfileEntity> builder)
    {
        builder.ToTable("cookie_profiles", "auth");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.OwnerSubject).HasColumnName("owner_subject").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ProfileKey).HasColumnName("profile_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Site).HasColumnName("site").HasMaxLength(255);
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(255);
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.OwnerSubject, x.ProfileKey })
            .HasDatabaseName("ux_cookie_profiles_owner_profile")
            .IsUnique();
    }
}
