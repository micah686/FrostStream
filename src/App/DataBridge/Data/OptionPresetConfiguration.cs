using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class OptionPresetConfiguration : IEntityTypeConfiguration<OptionPresetEntity>
{
    public void Configure(EntityTypeBuilder<OptionPresetEntity> builder)
    {
        builder.ToTable(
            "download_option_presets",
            "downloads",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_download_option_presets_key_format",
                    "\"key\" ~ '^[a-z0-9-]{2,100}$'");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.Key)
            .HasColumnName("key")
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(x => x.Key)
            .IsUnique()
            .HasDatabaseName("uq_download_option_presets_key");

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("description")
            .HasMaxLength(2000);

        builder.Property(x => x.YtDlpOptionsJson)
            .HasColumnName("ytdlp_options_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.Property(x => x.LastUpdated)
            .HasColumnName("last_updated")
            .HasColumnType("timestamp with time zone");
    }
}
