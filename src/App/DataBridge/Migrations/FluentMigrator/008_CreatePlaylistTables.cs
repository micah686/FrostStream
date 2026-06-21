using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(8, "Create playlists, playlist_items, playlist_scan_entries, media_playlist_membership in the playlists schema")]
public sealed class M008_CreatePlaylistTables : Migration
{
    private const string SchemaName = "playlists";
    private const string DownloadsSchema = "downloads";
    private const string MediaSchema = "media";

    public override void Up()
    {
        Create.Schema(SchemaName);

        Execute.Sql(
            "CREATE TYPE playlists.playlist_state AS ENUM (" +
            "'pending_metadata','metadata_resolved','failed');");

        Create.Table("playlists").InSchema(SchemaName)
            .WithColumn("playlist_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("state").AsCustom("playlists.playlist_state").NotNullable()
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
            .OnTable("playlists").InSchema(SchemaName)
            .OnColumn("state").Ascending()
            .OnColumn("updated_at").Ascending();

        Create.Index("ix_playlists_correlation_id")
            .OnTable("playlists").InSchema(SchemaName)
            .OnColumn("correlation_id").Ascending();

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_playlists_source_url ON playlists.playlists (source_url);");

        Create.Table("playlist_items").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_index").AsInt32().NotNullable()
            .WithColumn("entry_url").AsString(4096).NotNullable()
            .WithColumn("entry_title").AsString(2048).Nullable();

        Create.ForeignKey("fk_playlist_items_playlist_id")
            .FromTable("playlist_items").InSchema(SchemaName).ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema(SchemaName).PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_playlist_items_job_id")
            .FromTable("playlist_items").InSchema(SchemaName).ForeignColumn("job_id")
            .ToTable("download_jobs").InSchema(DownloadsSchema).PrimaryColumn("job_id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_playlist_items_playlist_id_index")
            .OnTable("playlist_items").InSchema(SchemaName)
            .OnColumn("playlist_id").Ascending()
            .OnColumn("playlist_index").Ascending();

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_playlist_items_job_id ON playlists.playlist_items (job_id);");

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_playlist_items_playlist_id_entry_url ON playlists.playlist_items (playlist_id, entry_url);");

        Create.Table("playlist_scan_entries").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_index").AsInt32().NotNullable()
            .WithColumn("entry_url").AsString(4096).NotNullable()
            .WithColumn("entry_title").AsString(2048).Nullable();

        Create.ForeignKey("fk_playlist_scan_entries_playlist_id")
            .FromTable("playlist_scan_entries").InSchema(SchemaName).ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema(SchemaName).PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_playlist_scan_entries_playlist_id_index")
            .OnTable("playlist_scan_entries").InSchema(SchemaName)
            .OnColumn("playlist_id").Ascending()
            .OnColumn("playlist_index").Ascending();

        Create.Table("media_playlist_membership").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("playlist_index").AsInt32().NotNullable();

        Create.ForeignKey("fk_media_playlist_membership_media_guid")
            .FromTable("media_playlist_membership").InSchema(SchemaName).ForeignColumn("media_guid")
            .ToTable("media").InSchema(MediaSchema).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_media_playlist_membership_playlist_id")
            .FromTable("media_playlist_membership").InSchema(SchemaName).ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema(SchemaName).PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_media_playlist_membership_playlist_id_index " +
            "ON playlists.media_playlist_membership (playlist_id, playlist_index);");

        Create.Index("ix_media_playlist_membership_media_guid")
            .OnTable("media_playlist_membership").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_media_playlist_membership_media_guid")
            .OnTable("media_playlist_membership").InSchema(SchemaName);
        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_media_playlist_membership_playlist_id_index;");
        Delete.ForeignKey("fk_media_playlist_membership_playlist_id")
            .OnTable("media_playlist_membership").InSchema(SchemaName);
        Delete.ForeignKey("fk_media_playlist_membership_media_guid")
            .OnTable("media_playlist_membership").InSchema(SchemaName);
        Delete.Table("media_playlist_membership").InSchema(SchemaName);

        Delete.Index("ix_playlist_scan_entries_playlist_id_index").OnTable("playlist_scan_entries").InSchema(SchemaName);
        Delete.ForeignKey("fk_playlist_scan_entries_playlist_id").OnTable("playlist_scan_entries").InSchema(SchemaName);
        Delete.Table("playlist_scan_entries").InSchema(SchemaName);

        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_playlist_items_playlist_id_entry_url;");
        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_playlist_items_job_id;");
        Delete.Index("ix_playlist_items_playlist_id_index").OnTable("playlist_items").InSchema(SchemaName);
        Delete.ForeignKey("fk_playlist_items_job_id").OnTable("playlist_items").InSchema(SchemaName);
        Delete.ForeignKey("fk_playlist_items_playlist_id").OnTable("playlist_items").InSchema(SchemaName);
        Delete.Table("playlist_items").InSchema(SchemaName);

        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_playlists_source_url;");
        Delete.Index("ix_playlists_correlation_id").OnTable("playlists").InSchema(SchemaName);
        Delete.Index("ix_playlists_state_updated_at").OnTable("playlists").InSchema(SchemaName);
        Delete.Table("playlists").InSchema(SchemaName);

        Execute.Sql("DROP TYPE IF EXISTS playlists.playlist_state;");

        Delete.Schema(SchemaName);
    }
}
