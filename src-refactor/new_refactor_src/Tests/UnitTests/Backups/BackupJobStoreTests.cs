using BackupService;
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
                Kind = BackupJobKinds.Backup,
                Name = "interrupted",
                Type = "full",
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
    public async Task Save_Persists_Idempotency_Key_And_Label_Across_Restarts()
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
                Kind = BackupJobKinds.Backup,
                Name = "scheduled",
                Type = "diff",
                Label = "20260803-020000F_20260804-020000D",
                Scheduled = true,
                IdempotencyKey = "backup-diff:backup-diff:2026-08-03T02:00:00Z",
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(job, CancellationToken.None);

            var restarted = new BackupJobStore(new BackupServiceOptions { Directory = root });
            await restarted.InitializeAsync(CancellationToken.None);

            var recovered = restarted.FindByIdempotencyKey(job.IdempotencyKey!).ShouldNotBeNull();
            recovered.JobId.ShouldBe(job.JobId);
            recovered.Label.ShouldBe(job.Label);
            recovered.Kind.ShouldBe(BackupJobKinds.Backup);
            recovered.Type.ShouldBe("diff");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), $"froststream-backup-tests-{Guid.NewGuid():N}");
}
