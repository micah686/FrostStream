using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class ScheduledTaskConfiguration : IEntityTypeConfiguration<ScheduledTaskEntity>
{
    public void Configure(EntityTypeBuilder<ScheduledTaskEntity> builder)
    {
        builder.ToTable(
            "scheduled_tasks",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_scheduled_tasks_key_format",
                    "\"key\" ~ '^[a-z0-9-]{2,100}$'");
                tableBuilder.HasCheckConstraint(
                    "ck_scheduled_tasks_cron_xor_interval",
                    "(cron IS NOT NULL) <> (interval_seconds IS NOT NULL)");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.Key)
            .HasColumnName("key")
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(x => x.Key)
            .IsUnique()
            .HasDatabaseName("uq_scheduled_tasks_key");

        builder.Property(x => x.TaskType)
            .HasColumnName("task_type")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Cron)
            .HasColumnName("cron")
            .HasMaxLength(255);

        builder.Property(x => x.IntervalSeconds)
            .HasColumnName("interval_seconds");

        builder.Property(x => x.Timezone)
            .HasColumnName("timezone")
            .HasMaxLength(100)
            .HasDefaultValue("UTC")
            .IsRequired();

        builder.Property(x => x.Enabled)
            .HasColumnName("enabled")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(x => x.CatchupPolicy)
            .HasColumnName("catchup_policy")
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasDefaultValue(ScheduleCatchupPolicy.Coalesce)
            .IsRequired();

        builder.Property(x => x.LastAttemptAt)
            .HasColumnName("last_attempt_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.LastSuccessAt)
            .HasColumnName("last_success_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.NextDueAt)
            .HasColumnName("next_due_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.Property(x => x.LastUpdated)
            .HasColumnName("last_updated")
            .HasColumnType("timestamp with time zone");
    }
}
