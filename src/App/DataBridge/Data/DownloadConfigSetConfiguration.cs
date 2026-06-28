using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class DownloadConfigSetConfiguration : IEntityTypeConfiguration<DownloadConfigSetEntity>
{
    public void Configure(EntityTypeBuilder<DownloadConfigSetEntity> builder)
    {
        builder.ToTable(
            "download_config_sets",
            "downloads",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_download_config_sets_key_format",
                    "\"key\" ~ '^[a-z0-9-]{2,100}$'");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.OwnerSubject).HasColumnName("owner_subject").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100);
        builder.Property(x => x.CookieProfileKey).HasColumnName("cookie_profile_key").HasMaxLength(100);
        builder.Property(x => x.YtDlpOptionsJson).HasColumnName("ytdlp_options_json").HasColumnType("jsonb");
        builder.Property(x => x.IgnoreKeywordsJson).HasColumnName("ignore_keywords_json").HasColumnType("jsonb");
        builder.Property(x => x.EncodeForPlaylist).HasColumnName("encode_for_playlist").HasDefaultValue(false).IsRequired();
        builder.Property(x => x.AudioFormat)
            .HasColumnName("audio_format")
            .HasColumnType("media.audio_rendition_format")
            .HasDefaultValue(AudioRenditionFormat.Aac)
            .IsRequired();
        builder.Property(x => x.Priority).HasColumnName("priority").HasDefaultValue(0).IsRequired();
        builder.Property(x => x.FetchComments).HasColumnName("fetch_comments").HasDefaultValue(false).IsRequired();
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(x => new { x.OwnerSubject, x.Key })
            .IsUnique()
            .HasDatabaseName("uq_download_config_sets_owner_key");
    }
}
