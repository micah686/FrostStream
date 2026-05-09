using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(8, "Create playlists, playlist_items, playlist_scan_entries (public) and metadata.media_playlist_membership tables")]
public sealed class M008_CreatePlaylistTables : Migration
{
    private const string MetadataSchema = "metadata";

    public override void Up()
    {
        Execute.Sql(
            "CREATE TYPE playlist_state AS ENUM (" +
            "'pending_metadata','metadata_resolved','failed');");

        Create.Table("playlists")
            .WithColumn("playlist_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("state").AsCustom("playlist_state").NotNullable()
            .WithColumn("source_url").AsString(4096).NotNullable()
            .WithColumn("requested_by").AsString(255).Nullable()
            .WithColumn("storage_key").AsString(100).Nullable()
            .WithColumn("provider_playlist_id").AsString(512).Nullable()
            .WithColumn("title").AsString(2048).Nullable()
            .WithColumn("total_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_scanned_at").AsCustom("timestamp with time zone").Nullable();

        Create.Index("ix_playlists_state_updated_at")
            .OnTable("playlists")
            .OnColumn("state").Ascending()
            .OnColumn("updated_at").Ascending();

        Create.Index("ix_playlists_correlation_id")
            .OnTable("playlists")
            .OnColumn("correlation_id").Ascending();

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_playlists_source_url ON playlists (source_url);");

        Create.Table("playlist_items")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_index").AsInt32().NotNullable()
            .WithColumn("entry_url").AsString(4096).NotNullable()
            .WithColumn("entry_title").AsString(2048).Nullable();

        Create.ForeignKey("fk_playlist_items_playlist_id")
            .FromTable("playlist_items").ForeignColumn("playlist_id")
            .ToTable("playlists").PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_playlist_items_job_id")
            .FromTable("playlist_items").ForeignColumn("job_id")
            .ToTable("download_jobs").PrimaryColumn("job_id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_playlist_items_playlist_id_index")
            .OnTable("playlist_items")
            .OnColumn("playlist_id").Ascending()
            .OnColumn("playlist_index").Ascending();

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_playlist_items_job_id ON playlist_items (job_id);");

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_playlist_items_playlist_id_entry_url ON playlist_items (playlist_id, entry_url);");

        Create.Table("playlist_scan_entries")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_index").AsInt32().NotNullable()
            .WithColumn("entry_url").AsString(4096).NotNullable()
            .WithColumn("entry_title").AsString(2048).Nullable();

        Create.ForeignKey("fk_playlist_scan_entries_playlist_id")
            .FromTable("playlist_scan_entries").ForeignColumn("playlist_id")
            .ToTable("playlists").PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_playlist_scan_entries_playlist_id_index")
            .OnTable("playlist_scan_entries")
            .OnColumn("playlist_id").Ascending()
            .OnColumn("playlist_index").Ascending();

        Create.Table("media_playlist_membership").InSchema(MetadataSchema)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_index").AsInt32().NotNullable();

        Create.ForeignKey("fk_media_playlist_membership_media_guid")
            .FromTable("media_playlist_membership").InSchema(MetadataSchema).ForeignColumn("media_guid")
            .ToTable("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_playlist_membership_playlist_id")
            .FromTable("media_playlist_membership").InSchema(MetadataSchema).ForeignColumn("playlist_id")
            .ToTable("playlists").PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_media_playlist_membership_playlist_id_index " +
            "ON metadata.media_playlist_membership (playlist_id, playlist_index);");

        Create.Index("ix_media_playlist_membership_media_guid")
            .OnTable("media_playlist_membership").InSchema(MetadataSchema)
            .OnColumn("media_guid").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_media_playlist_membership_media_guid")
            .OnTable("media_playlist_membership").InSchema(MetadataSchema);
        Execute.Sql("DROP INDEX IF EXISTS metadata.ux_media_playlist_membership_playlist_id_index;");
        Delete.ForeignKey("fk_media_playlist_membership_playlist_id")
            .OnTable("media_playlist_membership").InSchema(MetadataSchema);
        Delete.ForeignKey("fk_media_playlist_membership_media_guid")
            .OnTable("media_playlist_membership").InSchema(MetadataSchema);
        Delete.Table("media_playlist_membership").InSchema(MetadataSchema);

        Delete.Index("ix_playlist_scan_entries_playlist_id_index").OnTable("playlist_scan_entries");
        Delete.ForeignKey("fk_playlist_scan_entries_playlist_id").OnTable("playlist_scan_entries");
        Delete.Table("playlist_scan_entries");

        Execute.Sql("DROP INDEX IF EXISTS ux_playlist_items_playlist_id_entry_url;");
        Execute.Sql("DROP INDEX IF EXISTS ux_playlist_items_job_id;");
        Delete.Index("ix_playlist_items_playlist_id_index").OnTable("playlist_items");
        Delete.ForeignKey("fk_playlist_items_job_id").OnTable("playlist_items");
        Delete.ForeignKey("fk_playlist_items_playlist_id").OnTable("playlist_items");
        Delete.Table("playlist_items");

        Execute.Sql("DROP INDEX IF EXISTS ux_playlists_source_url;");
        Delete.Index("ix_playlists_correlation_id").OnTable("playlists");
        Delete.Index("ix_playlists_state_updated_at").OnTable("playlists");
        Delete.Table("playlists");

        Execute.Sql("DROP TYPE IF EXISTS playlist_state;");
    }
}
