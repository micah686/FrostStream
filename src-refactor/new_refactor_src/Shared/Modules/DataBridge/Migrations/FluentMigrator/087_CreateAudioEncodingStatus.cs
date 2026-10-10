using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// media.audio_renditions is job-queue-shaped (pending/processing/ready/failed, keyed to a specific
/// source content version) and its channel status/progress is recomputed by loading and diffing every
/// item in the channel on each call. This table is a separate, durable "is this media's audio encoded"
/// fact, one row per media item, kept in sync automatically as renditions complete but also directly
/// writable via API — independent of job/queue bookkeeping.
/// </summary>
[Migration(87, "Create durable per-media audio encoding status table")]
public sealed class M087_CreateAudioEncodingStatus : Migration
{
    public override void Up()
    {
        Create.Table("audio_encoding_status").InSchema("media")
            .WithColumn("media_guid").AsCustom("uuid").PrimaryKey()
            .WithColumn("account_id").AsInt64().NotNullable()
            .WithColumn("is_encoded").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("storage_key").AsString(100).Nullable()
            .WithColumn("storage_path").AsString(2048).Nullable()
            .WithColumn("encoded_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.ForeignKey("fk_audio_encoding_status_media_guid")
            .FromTable("audio_encoding_status").InSchema("media").ForeignColumn("media_guid")
            .ToTable("media").InSchema("media").PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_audio_encoding_status_account_id")
            .FromTable("audio_encoding_status").InSchema("media").ForeignColumn("account_id")
            .ToTable("accounts").InSchema("metadata").PrimaryColumn("id")
            .OnDelete(Rule.Cascade);

        Create.Index("ix_audio_encoding_status_account_encoded")
            .OnTable("audio_encoding_status").InSchema("media")
            .OnColumn("account_id").Ascending()
            .OnColumn("is_encoded").Ascending();

        // metadata.media_metadata has no index on account_id today, so any channel-scoped scan of it
        // (including the new paginated encoding-status list) would sequential-scan the whole table.
        Create.Index("ix_media_metadata_account_id")
            .OnTable("media_metadata").InSchema("metadata")
            .OnColumn("account_id").Ascending();

        // Backfill from renditions already marked Ready so the new table reflects reality immediately
        // instead of starting empty.
        Execute.Sql("""
            INSERT INTO media.audio_encoding_status (media_guid, account_id, is_encoded, storage_key, storage_path, encoded_at, updated_at)
            SELECT ar.media_guid, mm.account_id, true, ar.storage_key, ar.storage_path, ar.updated_at, ar.updated_at
            FROM media.audio_renditions ar
            JOIN metadata.media_metadata mm ON mm.media_guid = ar.media_guid
            WHERE ar.status = 'ready'
            ON CONFLICT (media_guid) DO NOTHING;
            """);
    }

    public override void Down()
    {
        Delete.Index("ix_media_metadata_account_id").OnTable("media_metadata").InSchema("metadata");
        Delete.Index("ix_audio_encoding_status_account_encoded").OnTable("audio_encoding_status").InSchema("media");
        Delete.ForeignKey("fk_audio_encoding_status_account_id").OnTable("audio_encoding_status").InSchema("media");
        Delete.ForeignKey("fk_audio_encoding_status_media_guid").OnTable("audio_encoding_status").InSchema("media");
        Delete.Table("audio_encoding_status").InSchema("media");
    }
}
