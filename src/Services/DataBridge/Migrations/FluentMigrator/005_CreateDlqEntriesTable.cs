using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(5)]
public class CreateDlqEntriesTable : Migration
{
    public override void Up()
    {
        Create.Table("dlq_entries")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("entry_key").AsString(500).NotNullable()
            .WithColumn("original_stream").AsString(255).NotNullable()
            .WithColumn("original_consumer").AsString(255).NotNullable()
            .WithColumn("original_subject").AsString(500).NotNullable()
            .WithColumn("original_sequence").AsInt64().NotNullable()
            .WithColumn("delivery_count").AsInt32().NotNullable()
            .WithColumn("failed_at").AsDateTime().NotNullable()
            .WithColumn("stored_at").AsDateTime().NotNullable()
            .WithColumn("error_reason").AsString().Nullable()
            .WithColumn("stack_trace").AsString().Nullable()
            .WithColumn("payload").AsString().Nullable()
            .WithColumn("payload_content_type").AsString(100).Nullable()
            .WithColumn("payload_size").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("message_type").AsString(500).Nullable()
            .WithColumn("correlation_id").AsString(255).Nullable()
            .WithColumn("job_id").AsGuid().Nullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("status_updated_at").AsDateTime().Nullable()
            .WithColumn("review_notes").AsString().Nullable();

        // Unique index on entry key for idempotency
        Create.Index("ix_dlq_entries_entry_key")
            .OnTable("dlq_entries")
            .OnColumn("entry_key")
            .Unique();

        // Index for querying by status
        Create.Index("ix_dlq_entries_status_stored")
            .OnTable("dlq_entries")
            .OnColumn("status").Ascending()
            .OnColumn("stored_at").Ascending();

        // Index for querying by stream/consumer
        Create.Index("ix_dlq_entries_stream_consumer")
            .OnTable("dlq_entries")
            .OnColumn("original_stream").Ascending()
            .OnColumn("original_consumer").Ascending()
            .OnColumn("stored_at").Ascending();

        // Index for job ID lookups
        Create.Index("ix_dlq_entries_job_id")
            .OnTable("dlq_entries")
            .OnColumn("job_id");

        // Index for correlation ID lookups
        Create.Index("ix_dlq_entries_correlation_id")
            .OnTable("dlq_entries")
            .OnColumn("correlation_id");
    }

    public override void Down()
    {
        Delete.Index("ix_dlq_entries_correlation_id").OnTable("dlq_entries");
        Delete.Index("ix_dlq_entries_job_id").OnTable("dlq_entries");
        Delete.Index("ix_dlq_entries_stream_consumer").OnTable("dlq_entries");
        Delete.Index("ix_dlq_entries_status_stored").OnTable("dlq_entries");
        Delete.Index("ix_dlq_entries_entry_key").OnTable("dlq_entries");

        Delete.Table("dlq_entries");
    }
}
