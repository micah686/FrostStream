using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Removes the legacy allow-list tables. Their rows are intentionally not copied: an allow-list
/// cannot be safely inverted into the deny semantics used by unified access policies.
/// </summary>
[Migration(68, "Drop legacy media access allow-list tables")]
public sealed class M068_DropLegacyMediaAccessTables : Migration
{
    public override void Up()
    {
        // Some development databases may already have run the earlier uncommitted form of
        // migration 067, which copied these allow-list rows into deterministic policy ids. Remove
        // those copies before dropping the source rows so they cannot become inverted deny rules.
        Execute.Sql("""
            DELETE FROM auth.access_policies p
            USING auth.media_access_restrictions legacy
            WHERE p.policy_id = (md5('media:' || legacy.media_guid::text || ':' || legacy.group_name))::uuid;

            DELETE FROM auth.access_policies p
            USING auth.provider_access_restrictions legacy
            WHERE p.policy_id = (md5('provider:' || legacy.provider_pattern || ':' || legacy.group_name))::uuid;

            DELETE FROM auth.access_policies p
            USING auth.age_limit_policies legacy
            WHERE p.policy_id = (md5('age:' || legacy.minimum_age_limit::text || ':' || legacy.group_name))::uuid;
            """);

        Delete.Table("age_limit_policies").InSchema("auth");
        Delete.Table("provider_access_restrictions").InSchema("auth");
        Delete.Table("media_access_restrictions").InSchema("auth");
    }

    public override void Down()
    {
        Create.Table("media_access_restrictions").InSchema("auth")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("media_guid").AsGuid().NotNullable()
            .WithColumn("group_name").AsString(255).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable();
        Create.Index("ux_media_access_restrictions_media_group")
            .OnTable("media_access_restrictions").InSchema("auth")
            .OnColumn("media_guid").Ascending()
            .OnColumn("group_name").Ascending()
            .WithOptions().Unique();
        Create.Index("ix_media_access_restrictions_media")
            .OnTable("media_access_restrictions").InSchema("auth")
            .OnColumn("media_guid");

        Create.Table("provider_access_restrictions").InSchema("auth")
            .WithColumn("provider_pattern").AsString(255).NotNullable()
            .WithColumn("group_name").AsString(255).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable();
        Create.PrimaryKey("pk_provider_access_restrictions")
            .OnTable("provider_access_restrictions").WithSchema("auth")
            .Columns("provider_pattern", "group_name");

        Create.Table("age_limit_policies").InSchema("auth")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("minimum_age_limit").AsInt32().NotNullable()
            .WithColumn("group_name").AsString(255).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable();
        Create.Index("ux_age_limit_policies_threshold_group")
            .OnTable("age_limit_policies").InSchema("auth")
            .OnColumn("minimum_age_limit").Ascending()
            .OnColumn("group_name").Ascending()
            .WithOptions().Unique();
    }
}
