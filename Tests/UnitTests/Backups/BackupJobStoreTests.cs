using BackupService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Backups;

public sealed class BackupJobStoreTests
{
    [Test]
    public async Task Initialize_Marks_Interrupted_Job_Failed()
    {
        var root = NewRoot();
        try
        {
            var store = new BackupJobStore(new BackupServiceOptions { Directory = root });
            await store.InitializeAsync(CancellationToken.None);
            var job = new BackupJobRecord
            {
                JobId = Guid.NewGuid(),
                Status = "running",
                Name = "interrupted",
                Mode = "snapshot",
                Scheduled = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(job, CancellationToken.None);

            var restarted = new BackupJobStore(new BackupServiceOptions { Directory = root });
            await restarted.InitializeAsync(CancellationToken.None);

            var recovered = restarted.Get(job.JobId).ShouldNotBeNull();
            recovered.Status.ShouldBe("failed");
            recovered.CompletedAt.ShouldNotBeNull();
            recovered.ErrorMessage.ShouldNotBeNull().ShouldContain("restarted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveArchive_Rejects_Path_Outside_Backup_Root()
    {
        var root = NewRoot();
        var outside = NewRoot();
        try
        {
            var store = new BackupJobStore(new BackupServiceOptions { Directory = root });
            await store.InitializeAsync(CancellationToken.None);
            Directory.CreateDirectory(outside);

            Should.Throw<ArgumentException>(() => store.ResolveArchive(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task Save_Persists_Idempotency_Key_Across_Restarts()
    {
        var root = NewRoot();
        try
        {
            var store = new BackupJobStore(new BackupServiceOptions { Directory = root });
            await store.InitializeAsync(CancellationToken.None);
            var job = new BackupJobRecord
            {
                JobId = Guid.NewGuid(),
                Status = "completed",
                Name = "scheduled",
                Mode = "snapshot",
                Scheduled = true,
                IdempotencyKey = "nightly:2026-07-17",
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(job, CancellationToken.None);

            var restarted = new BackupJobStore(new BackupServiceOptions { Directory = root });
            await restarted.InitializeAsync(CancellationToken.None);

            restarted.FindByIdempotencyKey(job.IdempotencyKey!).ShouldNotBeNull().JobId.ShouldBe(job.JobId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Retention_Deletes_Only_Scheduled_Snapshots_Beyond_Limit()
    {
        var root = NewRoot();
        try
        {
            var options = new BackupServiceOptions { Directory = root, ScheduledRetentionCount = 14 };
            var store = new BackupJobStore(options);
            await store.InitializeAsync(CancellationToken.None);
            for (var index = 0; index < 16; index++)
            {
                var archive = Path.Combine(store.Archives, $"scheduled-{index:00}");
                Directory.CreateDirectory(archive);
                await store.SaveAsync(new BackupJobRecord
                {
                    JobId = Guid.NewGuid(),
                    Status = "completed",
                    Name = Path.GetFileName(archive),
                    Mode = "snapshot",
                    Scheduled = true,
                    ArchivePath = archive,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-index),
                    CompletedAt = DateTimeOffset.UtcNow.AddDays(-index)
                }, CancellationToken.None);
            }

            var manualArchive = Path.Combine(store.Archives, "manual-old");
            Directory.CreateDirectory(manualArchive);
            await store.SaveAsync(new BackupJobRecord
            {
                JobId = Guid.NewGuid(),
                Status = "completed",
                Name = "manual-old",
                Mode = "snapshot",
                Scheduled = false,
                ArchivePath = manualArchive,
                CreatedAt = DateTimeOffset.UtcNow.AddYears(-1),
                CompletedAt = DateTimeOffset.UtcNow.AddYears(-1)
            }, CancellationToken.None);

            var catalog = new BackupArchiveCatalog(store, NullLogger<BackupArchiveCatalog>.Instance);
            var coordinator = new BackupCoordinator(
                store,
                catalog,
                Options.Create(options),
                new ConfigurationBuilder().Build(),
                NullLogger<BackupCoordinator>.Instance);

            await coordinator.PruneScheduledSnapshotsAsync(CancellationToken.None);

            Directory.EnumerateDirectories(store.Archives, "scheduled-*").Count().ShouldBe(14);
            Directory.Exists(manualArchive).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), $"froststream-backup-tests-{Guid.NewGuid():N}");
}
