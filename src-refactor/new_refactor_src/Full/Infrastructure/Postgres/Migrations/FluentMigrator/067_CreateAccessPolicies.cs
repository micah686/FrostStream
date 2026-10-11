using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(67, "Create unified access policies")]
public sealed class M067_CreateAccessPolicies : Migration
{
    public override void Up()
    {
        Create.Table("access_policies").InSchema("auth")
            .WithColumn("policy_id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("sync_status").AsString(32).NotNullable().WithDefaultValue("pending")
            .WithColumn("sync_error").AsString(2000).Nullable()
            .WithColumn("version").AsInt64().NotNullable().WithDefaultValue(1)
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by_subject").AsString(255).Nullable()
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_by_subject").AsString(255).Nullable();

        Execute.Sql("""
            CREATE UNIQUE INDEX ux_access_policies_name
            ON auth.access_policies (lower(name));
            """);

        Create.Table("access_policy_bundles").InSchema("auth")
            .WithColumn("policy_id").AsGuid().NotNullable()
            .WithColumn("bundle_id").AsString(255).NotNullable();
        Create.PrimaryKey("pk_access_policy_bundles").OnTable("access_policy_bundles").WithSchema("auth")
            .Columns("policy_id", "bundle_id");
        Create.ForeignKey("fk_access_policy_bundles_policy").FromTable("access_policy_bundles").InSchema("auth")
            .ForeignColumn("policy_id").ToTable("access_policies").InSchema("auth").PrimaryColumn("policy_id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Table("access_policy_media").InSchema("auth")
            .WithColumn("policy_id").AsGuid().NotNullable()
            .WithColumn("media_guid").AsGuid().NotNullable();
        Create.PrimaryKey("pk_access_policy_media").OnTable("access_policy_media").WithSchema("auth")
            .Columns("policy_id", "media_guid");
        Create.ForeignKey("fk_access_policy_media_policy").FromTable("access_policy_media").InSchema("auth")
            .ForeignColumn("policy_id").ToTable("access_policies").InSchema("auth").PrimaryColumn("policy_id")
            .OnDelete(System.Data.Rule.Cascade);
        Create.Index("ix_access_policy_media_guid").OnTable("access_policy_media").InSchema("auth")
            .OnColumn("media_guid");

        Create.Table("access_policy_providers").InSchema("auth")
            .WithColumn("policy_id").AsGuid().NotNullable()
            .WithColumn("provider").AsString(255).NotNullable();
        Create.PrimaryKey("pk_access_policy_providers").OnTable("access_policy_providers").WithSchema("auth")
            .Columns("policy_id", "provider");
        Create.ForeignKey("fk_access_policy_providers_policy").FromTable("access_policy_providers").InSchema("auth")
            .ForeignColumn("policy_id").ToTable("access_policies").InSchema("auth").PrimaryColumn("policy_id")
            .OnDelete(System.Data.Rule.Cascade);
        Create.Index("ix_access_policy_providers_provider").OnTable("access_policy_providers").InSchema("auth")
            .OnColumn("provider");

        Create.Table("access_policy_age_tiers").InSchema("auth")
            .WithColumn("policy_id").AsGuid().NotNullable()
            .WithColumn("minimum_age").AsInt32().NotNullable();
        Create.PrimaryKey("pk_access_policy_age_tiers").OnTable("access_policy_age_tiers").WithSchema("auth")
            .Columns("policy_id", "minimum_age");
        Create.ForeignKey("fk_access_policy_age_policy").FromTable("access_policy_age_tiers").InSchema("auth")
            .ForeignColumn("policy_id").ToTable("access_policies").InSchema("auth").PrimaryColumn("policy_id")
            .OnDelete(System.Data.Rule.Cascade);
        Create.Index("ix_access_policy_age_minimum").OnTable("access_policy_age_tiers").InSchema("auth")
            .OnColumn("minimum_age");

        Create.Table("access_policy_assignments").InSchema("auth")
            .WithColumn("policy_id").AsGuid().NotNullable()
            .WithColumn("principal_type").AsString(16).NotNullable()
            .WithColumn("principal_id").AsString(255).NotNullable();
        Create.PrimaryKey("pk_access_policy_assignments").OnTable("access_policy_assignments").WithSchema("auth")
            .Columns("policy_id", "principal_type", "principal_id");
        Create.ForeignKey("fk_access_policy_assignments_policy").FromTable("access_policy_assignments").InSchema("auth")
            .ForeignColumn("policy_id").ToTable("access_policies").InSchema("auth").PrimaryColumn("policy_id")
            .OnDelete(System.Data.Rule.Cascade);
        Create.Index("ix_access_policy_assignments_principal").OnTable("access_policy_assignments").InSchema("auth")
            .OnColumn("principal_type").Ascending()
            .OnColumn("principal_id").Ascending();

        Execute.Sql("""
            ALTER TABLE auth.access_policy_providers
                ADD CONSTRAINT ck_access_policy_providers_normalized
                CHECK (provider <> '' AND provider = lower(btrim(provider)));

            ALTER TABLE auth.access_policy_age_tiers
                ADD CONSTRAINT ck_access_policy_age_tiers_nonnegative
                CHECK (minimum_age >= 0);

            ALTER TABLE auth.access_policy_assignments
                ADD CONSTRAINT ck_access_policy_assignments_type
                CHECK (principal_type IN ('user', 'group'));

            COMMENT ON TABLE auth.access_policy_bundles IS
                'Endpoint bundles positively granted to principals assigned to the policy.';
            COMMENT ON TABLE auth.access_policy_media IS
                'Media GUID deny scopes. Assigned principals cannot watch matching media.';
            COMMENT ON TABLE auth.access_policy_providers IS
                'Normalized provider deny scopes. Assigned principals cannot watch matching providers.';
            COMMENT ON TABLE auth.access_policy_age_tiers IS
                'Inclusive deny thresholds. Assigned principals are denied when age_limit >= minimum_age.';
            COMMENT ON TABLE auth.access_policy_assignments IS
                'User and group principals to which endpoint grants and media denies apply.';
            """);
    }

    public override void Down()
    {
        Delete.Table("access_policy_assignments").InSchema("auth");
        Delete.Table("access_policy_age_tiers").InSchema("auth");
        Delete.Table("access_policy_providers").InSchema("auth");
        Delete.Table("access_policy_media").InSchema("auth");
        Delete.Table("access_policy_bundles").InSchema("auth");
        Delete.Index("ux_access_policies_name").OnTable("access_policies").InSchema("auth");
        Delete.Table("access_policies").InSchema("auth");
    }
}
