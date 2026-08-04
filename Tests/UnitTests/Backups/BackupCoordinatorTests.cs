using BackupService;
using BackupService.PgBackRest;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Backups;

public sealed class BackupCoordinatorTests
{
    [Test]
    public async Task Backup_Job_Runs_Engine_Pairs_OpenBao_And_Stores_Label()
    {
        await using var harness = await Harness.StartAsync();

        var queued = await harness.Coordinator.QueueBackupAsync(
            "pre upgrade!", "diff", scheduled: false, scheduleKey: null, idempotencyKey: null, CancellationToken.None);
        var completed = await harness.Coordinator.WaitAsync(queued.JobId, CancellationToken.None);

        completed.Status.ShouldBe("completed");
        completed.Kind.ShouldBe(BackupJobKinds.Backup);
        completed.Type.ShouldBe("diff");
        completed.Name.ShouldBe("pre-upgrade");
        completed.Label.ShouldBe(Harness.NewestLabel);
        harness.Runner.BackupTypes.ShouldBe(["diff"]);
        harness.Pairing.ExportedLabels.ShouldBe([Harness.NewestLabel]);
        harness.Pairing.PrunedTo.ShouldNotBeNull().ShouldContain(Harness.NewestLabel);
        harness.Coordinator.GetProgress(queued.JobId).ShouldContain(line => line.Contains("engine output"));
    }

    [Test]
    public async Task Duplicate_Idempotency_Key_Returns_Existing_Job()
    {
        await using var harness = await Harness.StartAsync();

        var first = await harness.Coordinator.QueueBackupAsync(
            null, "full", scheduled: true, scheduleKey: "backup-full", idempotencyKey: "key-1", CancellationToken.None);
        await harness.Coordinator.WaitAsync(first.JobId, CancellationToken.None);

        var second = await harness.Coordinator.QueueBackupAsync(
            null, "full", scheduled: true, scheduleKey: "backup-full", idempotencyKey: "key-1", CancellationToken.None);

        second.JobId.ShouldBe(first.JobId);
        harness.Runner.BackupTypes.Count.ShouldBe(1);
    }

    [Test]
    public async Task Invalid_Backup_Type_Is_Rejected()
    {
        await using var harness = await Harness.StartAsync();

        await Should.ThrowAsync<ArgumentException>(() => harness.Coordinator.QueueBackupAsync(
            null, "incremental", scheduled: false, scheduleKey: null, idempotencyKey: null, CancellationToken.None));
    }

    [Test]
    public async Task Verify_Jobs_Dispatch_By_Depth()
    {
        await using var harness = await Harness.StartAsync();

        var quick = await harness.Coordinator.QueueVerifyAsync("ignored-label", deep: false, CancellationToken.None);
        var deep = await harness.Coordinator.QueueVerifyAsync(Harness.NewestLabel, deep: true, CancellationToken.None);
        (await harness.Coordinator.WaitAsync(quick.JobId, CancellationToken.None)).Status.ShouldBe("completed");
        (await harness.Coordinator.WaitAsync(deep.JobId, CancellationToken.None)).Status.ShouldBe("completed");

        harness.Runner.VerifyCalls.ShouldBe(1);
        harness.DeepVerify.Labels.ShouldBe([Harness.NewestLabel]);
        // Quick verify is repository-wide; the label must not be recorded against the job.
        quick.Label.ShouldBeNull();
    }

    [Test]
    public async Task Restore_Refuses_While_Postgres_Is_Running()
    {
        await using var harness = await Harness.StartAsync();
        harness.Probe.Running = true;

        var job = await harness.Coordinator.QueueRestoreAsync(null, null, CancellationToken.None);
        var completed = await harness.Coordinator.WaitAsync(job.JobId, CancellationToken.None);

        completed.Status.ShouldBe("failed");
        completed.ErrorMessage.ShouldNotBeNull().ShouldContain("still running");
        harness.Runner.RestoreCalls.ShouldBe(0);
    }

    [Test]
    public async Task Restore_Passes_Label_And_Target_To_The_Engine()
    {
        await using var harness = await Harness.StartAsync();
        var target = DateTimeOffset.UtcNow.AddHours(-1);

        var job = await harness.Coordinator.QueueRestoreAsync(Harness.NewestLabel, target, CancellationToken.None);
        var completed = await harness.Coordinator.WaitAsync(job.JobId, CancellationToken.None);

        completed.Status.ShouldBe("completed");
        harness.Runner.RestoreCalls.ShouldBe(1);
        harness.Runner.LastRestoreLabel.ShouldBe(Harness.NewestLabel);
        harness.Runner.LastRestoreTarget.ShouldBe(target);
        harness.Runner.LastRestorePgData.ShouldBe(harness.Options.PgDataPath);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string NewestLabel = "20260801-030000F";

        public required BackupServiceOptions Options { get; init; }
        public required BackupCoordinator Coordinator { get; init; }
        public required FakeRunner Runner { get; init; }
        public required FakeDeepVerify DeepVerify { get; init; }
        public required FakePairing Pairing { get; init; }
        public required FakeProbe Probe { get; init; }
        public required string Root { get; init; }

        public static async Task<Harness> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"froststream-coordinator-tests-{Guid.NewGuid():N}");
            var options = new BackupServiceOptions { Directory = root, PgDataPath = Path.Combine(root, "pgdata") };
            var store = new BackupJobStore(options);
            var runner = new FakeRunner(options);
            var deepVerify = new FakeDeepVerify(options, runner);
            var pairing = new FakePairing(options);
            var probe = new FakeProbe(options);
            var coordinator = new BackupCoordinator(
                store,
                runner,
                deepVerify,
                pairing,
                probe,
                options,
                NullBackgroundRunReporter.Instance,
                NullLogger<BackupCoordinator>.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            return new Harness
            {
                Options = options,
                Coordinator = coordinator,
                Runner = runner,
                DeepVerify = deepVerify,
                Pairing = pairing,
                Probe = probe,
                Root = root
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.StopAsync(CancellationToken.None);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeRunner(BackupServiceOptions options)
        : PgBackRestRunner(options, NullLogger<PgBackRestRunner>.Instance)
    {
        public List<string> BackupTypes { get; } = [];
        public int VerifyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public string? LastRestoreLabel { get; private set; }
        public DateTimeOffset? LastRestoreTarget { get; private set; }
        public string? LastRestorePgData { get; private set; }

        public override Task<PgBackRestStanzaInfo?> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult<PgBackRestStanzaInfo?>(new PgBackRestStanzaInfo
            {
                Name = "froststream",
                Status = new PgBackRestStatus { Code = 0, Message = "ok" },
                Backup = [new PgBackRestBackup { Label = Harness.NewestLabel, Type = "full" }]
            });

        public override Task<string> BackupAsync(
            string type, string name, IProgress<string> progress, CancellationToken cancellationToken)
        {
            BackupTypes.Add(type);
            progress.Report("engine output");
            return Task.FromResult(Harness.NewestLabel);
        }

        public override Task VerifyAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            VerifyCalls++;
            return Task.CompletedTask;
        }

        public override Task RestoreAsync(
            string? label,
            DateTimeOffset? targetTime,
            string pgDataPath,
            bool immediateTarget,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            LastRestoreLabel = label;
            LastRestoreTarget = targetTime;
            LastRestorePgData = pgDataPath;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeepVerify(BackupServiceOptions options, PgBackRestRunner runner)
        : DeepVerifyRunner(options, runner, NullLogger<DeepVerifyRunner>.Instance)
    {
        public List<string?> Labels { get; } = [];

        public override Task RunAsync(string? label, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Labels.Add(label);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePairing(BackupServiceOptions options)
        : OpenBaoPairing(options, NullLogger<OpenBaoPairing>.Instance)
    {
        public List<string> ExportedLabels { get; } = [];
        public IReadOnlyCollection<string>? PrunedTo { get; private set; }

        public override Task ExportForBackupAsync(string label, CancellationToken cancellationToken)
        {
            ExportedLabels.Add(label);
            return Task.CompletedTask;
        }

        public override void PruneExcept(IReadOnlyCollection<string> liveLabels)
            => PrunedTo = liveLabels;
    }

    private sealed class FakeProbe(BackupServiceOptions options) : PostgresStateProbe(options)
    {
        public bool Running { get; set; }

        public override Task<PostgresState> ProbeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PostgresState(Running, StalePidFile: false));
    }
}
