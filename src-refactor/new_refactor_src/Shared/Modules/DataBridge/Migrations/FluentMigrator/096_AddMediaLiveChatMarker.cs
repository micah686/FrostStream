using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Marker table recording which media have their live-chat replay ingested into ClickHouse.
/// Kept in Postgres so the watch-detail read path can report <c>HasLiveChat</c> without ever
/// touching ClickHouse; deployments without the optional live-chat feature simply have no rows.
/// </summary>
[Migration(96, "Add metadata.media_live_chat ingestion marker table")]
public sealed class M096_AddMediaLiveChatMarker : Migration
{
    private const string SchemaName = "metadata";
    private const string TableName = "media_live_chat";

    public override void Up()
    {
        Create.Table(TableName).InSchema(SchemaName)
            .WithColumn("media_guid").AsGuid().PrimaryKey()
            .WithColumn("version_num").AsInt32().Nullable()
            .WithColumn("message_count").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("first_offset_ms").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("last_offset_ms").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("ingested_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
    }

    public override void Down()
    {
        Delete.Table(TableName).InSchema(SchemaName);
    }
}
