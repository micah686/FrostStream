using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(13, "Create creator source discovery tables")]
public sealed class M013_CreateCreatorDiscoveryTables : Migration
{
    private const string SchemaName = "discovery";

    public override void Up()
    {
        Create.Schema(SchemaName);

        Create.Table("creator_sources").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("platform").AsString(50).NotNullable()
            .WithColumn("source_type").AsString(50).NotNullable()
            .WithColumn("source_url").AsString(4096).NotNullable()
            .WithColumn("scan_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("incremental_page_size").AsInt32().NotNullable().WithDefaultValue(50)
            .WithColumn("consecutive_known_threshold").AsInt32().NotNullable().WithDefaultValue(25)
            .WithColumn("full_rescan_interval_days").AsInt32().NotNullable().WithDefaultValue(30)
            .WithColumn("metadata_refresh_window").AsInt32().NotNullable().WithDefaultValue(25)
            .WithColumn("last_successful_scan_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_full_scan_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_seen_high_watermark").AsString(512).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.UniqueConstraint("uq_creator_sources_source_url")
            .OnTable("creator_sources").WithSchema(SchemaName)
            .Column("source_url");

        Create.Index("ix_creator_sources_scan_enabled")
            .OnTable("creator_sources").InSchema(SchemaName)
            .OnColumn("scan_enabled").Ascending();

        Execute.Sql(
            "ALTER TABLE discovery.creator_sources ADD CONSTRAINT ck_creator_sources_scan_settings_positive " +
            "CHECK (incremental_page_size > 0 AND consecutive_known_threshold > 0 AND full_rescan_interval_days > 0 AND metadata_refresh_window > 0);");

        Create.Table("discovered_media").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("creator_source_id").AsInt64().NotNullable()
            .WithColumn("platform").AsString(50).NotNullable()
            .WithColumn("extractor").AsString(255).NotNullable()
            .WithColumn("external_media_id").AsString(512).NotNullable()
            .WithColumn("canonical_url").AsString(4096).NotNullable()
            .WithColumn("title").AsString(2048).Nullable()
            .WithColumn("duration_seconds").AsDouble().Nullable()
            .WithColumn("thumbnail_url").AsString(4096).Nullable()
            .WithColumn("live_status").AsString(100).Nullable()
            .WithColumn("availability").AsString(100).Nullable()
            .WithColumn("discovery_status").AsString(50).NotNullable().WithDefaultValue("Discovered")
            .WithColumn("metadata_status").AsString(50).NotNullable().WithDefaultValue("PendingEnrichment")
            .WithColumn("first_seen_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_seen_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("missed_full_scan_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("last_changed_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_enqueued_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.ForeignKey("fk_discovered_media_creator_source_id")
            .FromTable("discovered_media").InSchema(SchemaName).ForeignColumn("creator_source_id")
            .ToTable("creator_sources").InSchema(SchemaName).PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_discovered_media_identity " +
            "ON discovery.discovered_media (platform, extractor, external_media_id);");

        Create.Index("ix_discovered_media_creator_source_id")
            .OnTable("discovered_media").InSchema(SchemaName)
            .OnColumn("creator_source_id").Ascending();

        Create.Index("ix_discovered_media_metadata_status")
            .OnTable("discovered_media").InSchema(SchemaName)
            .OnColumn("metadata_status").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_discovered_media_metadata_status").OnTable("discovered_media").InSchema(SchemaName);
        Delete.Index("ix_discovered_media_creator_source_id").OnTable("discovered_media").InSchema(SchemaName);
        Execute.Sql("DROP INDEX IF EXISTS discovery.ux_discovered_media_identity;");
        Delete.ForeignKey("fk_discovered_media_creator_source_id").OnTable("discovered_media").InSchema(SchemaName);
        Delete.Table("discovered_media").InSchema(SchemaName);

        Delete.Index("ix_creator_sources_scan_enabled").OnTable("creator_sources").InSchema(SchemaName);
        Delete.UniqueConstraint("uq_creator_sources_source_url").FromTable("creator_sources").InSchema(SchemaName);
        Delete.Table("creator_sources").InSchema(SchemaName);

        Delete.Schema(SchemaName);
    }
}
