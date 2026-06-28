using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class PlaylistConfiguration : IEntityTypeConfiguration<PlaylistEntity>
{
    public void Configure(EntityTypeBuilder<PlaylistEntity> builder)
    {
        builder.ToTable("playlists", "playlists");
        builder.HasKey(x => x.PlaylistId);

        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(x => x.State).HasColumnName("state").HasColumnType("playlists.playlist_state").IsRequired();
        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(255);
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100);
        builder.Property(x => x.EncodeForPlaylist).HasColumnName("encode_for_playlist").HasDefaultValue(false).IsRequired();
        builder.Property(x => x.AudioFormat)
            .HasColumnName("audio_format")
            .HasColumnType("media.audio_rendition_format")
            .HasDefaultValue(AudioRenditionFormat.Aac)
            .IsRequired();
        builder.Property(x => x.ProviderPlaylistId).HasColumnName("provider_playlist_id").HasMaxLength(512);
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(2048);
        builder.Property(x => x.TotalItems).HasColumnName("total_items").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedOnAdd().IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastScannedAt).HasColumnName("last_scanned_at").HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.State, x.UpdatedAt }).HasDatabaseName("ix_playlists_state_updated_at");
        builder.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_playlists_correlation_id");
        builder.HasIndex(x => x.SourceUrl).IsUnique().HasDatabaseName("ux_playlists_source_url");
    }
}

public sealed class PlaylistItemConfiguration : IEntityTypeConfiguration<PlaylistItemEntity>
{
    public void Configure(EntityTypeBuilder<PlaylistItemEntity> builder)
    {
        builder.ToTable("playlist_items", "playlists");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").IsRequired();
        builder.Property(x => x.JobId).HasColumnName("job_id").IsRequired();
        builder.Property(x => x.PlaylistIndex).HasColumnName("playlist_index").IsRequired();
        builder.Property(x => x.EntryUrl).HasColumnName("entry_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.EntryTitle).HasColumnName("entry_title").HasMaxLength(2048);

        builder.HasIndex(x => new { x.PlaylistId, x.PlaylistIndex }).HasDatabaseName("ix_playlist_items_playlist_id_index");
        builder.HasIndex(x => x.JobId).IsUnique().HasDatabaseName("ux_playlist_items_job_id");
        builder.HasIndex(x => new { x.PlaylistId, x.EntryUrl }).IsUnique().HasDatabaseName("ux_playlist_items_playlist_id_entry_url");

        builder.HasOne<PlaylistEntity>()
            .WithMany()
            .HasForeignKey(x => x.PlaylistId)
            .HasConstraintName("fk_playlist_items_playlist_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DownloadJobEntity>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .HasConstraintName("fk_playlist_items_job_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PlaylistScanEntryConfiguration : IEntityTypeConfiguration<PlaylistScanEntryEntity>
{
    public void Configure(EntityTypeBuilder<PlaylistScanEntryEntity> builder)
    {
        builder.ToTable("playlist_scan_entries", "playlists");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").IsRequired();
        builder.Property(x => x.PlaylistIndex).HasColumnName("playlist_index").IsRequired();
        builder.Property(x => x.EntryUrl).HasColumnName("entry_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.EntryTitle).HasColumnName("entry_title").HasMaxLength(2048);

        builder.HasIndex(x => new { x.PlaylistId, x.PlaylistIndex }).HasDatabaseName("ix_playlist_scan_entries_playlist_id_index");

        builder.HasOne<PlaylistEntity>()
            .WithMany()
            .HasForeignKey(x => x.PlaylistId)
            .HasConstraintName("fk_playlist_scan_entries_playlist_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MediaPlaylistMembershipConfiguration : IEntityTypeConfiguration<MediaPlaylistMembershipEntity>
{
    public void Configure(EntityTypeBuilder<MediaPlaylistMembershipEntity> builder)
    {
        builder.ToTable("media_playlist_membership", "playlists");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid").IsRequired();
        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").IsRequired();
        builder.Property(x => x.PlaylistIndex).HasColumnName("playlist_index").IsRequired();

        builder.HasIndex(x => new { x.PlaylistId, x.PlaylistIndex })
            .IsUnique()
            .HasDatabaseName("ux_media_playlist_membership_playlist_id_index");

        builder.HasIndex(x => x.MediaGuid).HasDatabaseName("ix_media_playlist_membership_media_guid");

        builder.HasOne<MediaEntity>()
            .WithMany()
            .HasForeignKey(x => x.MediaGuid)
            .HasConstraintName("fk_media_playlist_membership_media_guid")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<PlaylistEntity>()
            .WithMany()
            .HasForeignKey(x => x.PlaylistId)
            .HasConstraintName("fk_media_playlist_membership_playlist_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PlaylistMetadataConfiguration : IEntityTypeConfiguration<PlaylistMetadataEntity>
{
    public void Configure(EntityTypeBuilder<PlaylistMetadataEntity> builder)
    {
        builder.ToTable("playlist_metadata", "playlists");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title");

        builder.HasIndex(x => x.PlaylistId)
            .IsUnique()
            .HasDatabaseName("ux_metadata_playlist_metadata_playlist_id");

        builder.HasOne<PlaylistEntity>()
            .WithMany()
            .HasForeignKey(x => x.PlaylistId)
            .HasConstraintName("fk_metadata_playlist_metadata_playlist_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserPlaylistConfiguration : IEntityTypeConfiguration<UserPlaylistEntity>
{
    public void Configure(EntityTypeBuilder<UserPlaylistEntity> builder)
    {
        builder.ToTable("user_playlists", "playlists");
        builder.HasKey(x => x.PlaylistId);

        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").ValueGeneratedNever();
        builder.Property(x => x.OwnerSubject).HasColumnName("owner_subject").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2048);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedOnAdd().IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(x => new { x.OwnerSubject, x.CreatedAt }).HasDatabaseName("ix_user_playlists_owner_created_at");
    }
}

public sealed class UserPlaylistItemConfiguration : IEntityTypeConfiguration<UserPlaylistItemEntity>
{
    public void Configure(EntityTypeBuilder<UserPlaylistItemEntity> builder)
    {
        builder.ToTable("user_playlist_items", "playlists");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.PlaylistId).HasColumnName("playlist_id").IsRequired();
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid").IsRequired();
        builder.Property(x => x.Position).HasColumnName("position").IsRequired();
        builder.Property(x => x.AddedAt).HasColumnName("added_at").HasColumnType("timestamp with time zone").ValueGeneratedOnAdd().IsRequired();

        builder.HasIndex(x => new { x.PlaylistId, x.Position })
            .IsUnique()
            .HasDatabaseName("ux_user_playlist_items_playlist_position");

        builder.HasIndex(x => new { x.PlaylistId, x.MediaGuid })
            .IsUnique()
            .HasDatabaseName("ux_user_playlist_items_playlist_media");

        builder.HasOne<UserPlaylistEntity>()
            .WithMany()
            .HasForeignKey(x => x.PlaylistId)
            .HasConstraintName("fk_user_playlist_items_playlist_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<MediaEntity>()
            .WithMany()
            .HasForeignKey(x => x.MediaGuid)
            .HasConstraintName("fk_user_playlist_items_media_guid")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
