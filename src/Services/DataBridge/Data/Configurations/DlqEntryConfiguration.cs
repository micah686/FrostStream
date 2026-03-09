using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class DlqEntryConfiguration : IEntityTypeConfiguration<DlqEntry>
{
    public void Configure(EntityTypeBuilder<DlqEntry> builder)
    {
        builder.ToTable("dlq_entries");

        // Unique index on the entry key for idempotency
        builder.HasIndex(x => x.EntryKey).IsUnique();

        // Index for querying by status
        builder.HasIndex(x => new { x.Status, x.StoredAt })
            .HasDatabaseName("ix_dlq_entries_status_stored");

        // Index for querying by stream/consumer
        builder.HasIndex(x => new { x.OriginalStream, x.OriginalConsumer, x.StoredAt })
            .HasDatabaseName("ix_dlq_entries_stream_consumer");

        // Index for querying by job ID
        builder.HasIndex(x => x.JobId)
            .HasDatabaseName("ix_dlq_entries_job_id");

        // Index for correlation ID lookups
        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_dlq_entries_correlation_id");

        // Configure enum conversion
        builder.Property(x => x.Status)
            .HasConversion<int>();

        // Store UTC timestamps
        builder.Property(x => x.FailedAt)
            .HasConversion(
                v => v.UtcDateTime,
                v => new DateTimeOffset(v, TimeSpan.Zero));

        builder.Property(x => x.StoredAt)
            .HasConversion(
                v => v.UtcDateTime,
                v => new DateTimeOffset(v, TimeSpan.Zero));

        builder.Property(x => x.StatusUpdatedAt)
            .HasConversion(
                v => v.HasValue ? v.Value.UtcDateTime : (DateTime?)null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null);
    }
}
