using DataBridge.Data;
using DataBridge.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using Conduit.NATS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Npgsql;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;
using Testcontainers.Nats;
using TUnit.Core;

namespace IntegrationTests.DataBridge;

/// <summary>
/// End-to-end verification of the download-queue read surface over a real NATS server + Postgres:
/// the exact wire round-trip WebAPI performs (core request/reply with the shared serializer registry)
/// answered by the real <see cref="DownloadQueueConsumerService"/>, with the production
/// <c>download.&gt;</c> JetStream topology provisioned so subject capture matches the live stack.
/// Also proves the non-persistent <see cref="DownloadQueueStateChanged"/> broadcast reaches a core
/// subscriber, which is what <c>DownloadQueueHub</c> relies on for SSE state events.
/// </summary>
public sealed class DownloadQueueFlowTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly QueueStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static DownloadQueueFlowTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task ResetAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        await Fixture.ResetAsync();
    }

    [After(Test)]
    public void Release()
    {
        Gate.Release();
    }

    [Test]
    public async Task List_Get_And_History_RoundTrip_Over_Real_Nats()
    {
        var completedId = Guid.NewGuid();
        var queuedId = Guid.NewGuid();
        await Fixture.SeedJobsAsync(
            Job(completedId, DownloadJobState.Completed, requestedBy: "alice"),
            Job(queuedId, DownloadJobState.Queued, sourceUrl: "https://example.test/other"));
        await Fixture.RecordHistoryAsync(completedId, nameof(DownloadRequested), nameof(DownloadCompleted));

        var list = await Fixture.Bus.RequestAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
            DownloadQueueSubjects.List, new DownloadQueueListRequest(), RequestTimeout);
        list.ShouldNotBeNull();
        list.Success.ShouldBeTrue(list.ErrorMessage);
        list.TotalCount.ShouldBe(2);
        list.Items.Select(x => x.JobId).ShouldBe([completedId, queuedId], ignoreOrder: true);

        var filtered = await Fixture.Bus.RequestAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
            DownloadQueueSubjects.List,
            new DownloadQueueListRequest { State = DownloadJobState.Completed, RequestedBy = "alice" },
            RequestTimeout);
        filtered.ShouldNotBeNull();
        filtered.Success.ShouldBeTrue(filtered.ErrorMessage);
        filtered.Items.ShouldHaveSingleItem().JobId.ShouldBe(completedId);

        var get = await Fixture.Bus.RequestAsync<DownloadQueueGetRequest, DownloadQueueGetResponse>(
            DownloadQueueSubjects.Get, new DownloadQueueGetRequest { JobId = completedId }, RequestTimeout);
        get.ShouldNotBeNull();
        get.Success.ShouldBeTrue(get.ErrorMessage);
        get.Job.ShouldNotBeNull();
        get.Job.State.ShouldBe(DownloadJobState.Completed);
        get.Job.RequestedBy.ShouldBe("alice");

        var missing = await Fixture.Bus.RequestAsync<DownloadQueueGetRequest, DownloadQueueGetResponse>(
            DownloadQueueSubjects.Get, new DownloadQueueGetRequest { JobId = Guid.NewGuid() }, RequestTimeout);
        missing.ShouldNotBeNull();
        missing.Success.ShouldBeFalse();
        missing.ErrorCode.ShouldBe("not_found");

        var history = await Fixture.Bus.RequestAsync<DownloadQueueHistoryRequest, DownloadQueueHistoryResponse>(
            DownloadQueueSubjects.History, new DownloadQueueHistoryRequest { JobId = completedId }, RequestTimeout);
        history.ShouldNotBeNull();
        history.Success.ShouldBeTrue(history.ErrorMessage);
        history.Entries.Select(x => x.EventName).ShouldBe([nameof(DownloadRequested), nameof(DownloadCompleted)]);
    }

    [Test]
    public async Task V2_Stage_Transition_Broadcasts_StateChanged_To_Core_Subscriber()
    {
        var jobId = Guid.NewGuid();
        var run = await Fixture.CreateRunAsync(jobId);

        var received = new TaskCompletionSource<DownloadQueueStateChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await Fixture.Bus.SubscribeAsync<DownloadQueueStateChanged>(
            DownloadQueueSubjects.StateChanged,
            context =>
            {
                received.TrySetResult(context.Message);
                return Task.CompletedTask;
            });

        await Fixture.BeginMetadataAttemptAsync(run);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        evt.JobId.ShouldBe(jobId);
        evt.Status.ShouldBe(DownloadJobStatus.Running);
        evt.Stage.ShouldBe(DownloadStage.Metadata);
        evt.StageStatus.ShouldBe(DownloadStageStatus.Pending);
        evt.RunId.ShouldBe(run.RunId);
        evt.Attempt.ShouldBe(1);
    }

    private static DownloadJobEntity Job(
        Guid jobId,
        DownloadJobState state,
        string sourceUrl = "https://example.test/video",
        string? requestedBy = null)
        => new()
        {
            JobId = jobId,
            CorrelationId = Guid.NewGuid(),
            State = state,
            Status = state == DownloadJobState.Completed ? DownloadJobStatus.Completed : DownloadJobStatus.Queued,
            SourceUrl = sourceUrl,
            RequestedBy = requestedBy,
            StorageKey = "default",
            SourceKind = DownloadSourceKind.Direct,
            IngestOrigin = IngestOrigin.Download
        };

    /// <summary>
    /// Real NATS (JetStream enabled) + Postgres hosting the production DataBridge pieces of the
    /// queue surface: migrations, <see cref="DownloadJobsRepository"/> with the real
    /// <see cref="DownloadJobStateNotifier"/>, <see cref="DownloadQueueConsumerService"/>, and the
    /// provisioned <see cref="DownloadTopology"/> stream.
    /// </summary>
    private sealed class QueueStackFixture : IAsyncDisposable
    {
        private readonly NatsContainer _natsContainer = new NatsBuilder("nats:2.10")
            .WithCommand("--jetstream")
            .Build();

        private readonly IContainer _postgresContainer = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_DB", "froststream_download_queue_flow_tests")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithPortBinding(5432, true)
            .Build();

        private IHost? _host;
        private bool _initialized;

        public IMessageBus Bus => _host!.Services.GetRequiredService<IMessageBus>();

        private string ConnectionString =>
            new NpgsqlConnectionStringBuilder
            {
                Host = _postgresContainer.Hostname,
                Port = _postgresContainer.GetMappedPublicPort(5432),
                Database = "froststream_download_queue_flow_tests",
                Username = "postgres",
                Password = "postgres",
                SearchPath = "storage,downloads,media,maintenance,metadata,auth,public"
            }.ConnectionString;

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            // The podman API socket occasionally resets the first heavy connection; StartAsync is
            // idempotent for already-running containers, so retrying the whole sequence is safe.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _natsContainer.StartAsync();
                    await _postgresContainer.StartAsync();
                    break;
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(1000);
                }
            }

            await WaitForPostgresAsync();
            await StartHostAsync();
            _initialized = true;
        }

        public async Task ResetAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "TRUNCATE TABLE download_jobs, download_job_history RESTART IDENTITY CASCADE;",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedJobsAsync(params DownloadJobEntity[] jobs)
        {
            await using var scope = _host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            db.DownloadJobs.AddRange(jobs);
            await db.SaveChangesAsync();
        }

        public async Task RecordHistoryAsync(Guid jobId, params string[] eventNames)
        {
            await using var scope = _host!.Services.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            foreach (var eventName in eventNames)
            {
                await repo.RecordHistoryAsync(jobId, Guid.NewGuid(), $"op/{eventName}", eventName, "{}");
            }
        }

        public async Task<DownloadRunRequest> CreateRunAsync(Guid jobId)
        {
            await using var scope = _host!.Services.CreateAsyncScope();
            var correlationId = Guid.NewGuid();
            var run = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .CreateInitialRunAsync(new DownloadRequested
                {
                    JobId = jobId,
                    CorrelationId = correlationId,
                    MessageId = Guid.NewGuid(),
                    OperationKey = $"job/{jobId:N}/requested",
                    OccurredAt = SystemClock.Instance.GetCurrentInstant(),
                    Attempt = 1,
                    SourceUrl = "https://example.test/video",
                    StorageKey = "default"
                }, autoStart: true);
            return run ?? throw new InvalidOperationException("Expected a fresh V2 run.");
        }

        public async Task BeginMetadataAttemptAsync(DownloadRunRequest run)
        {
            await using var scope = _host!.Services.CreateAsyncScope();
            var dispatchId = Guid.NewGuid();
            var execution = new DownloadExecutionIdentity
            {
                JobId = run.Request.JobId,
                RunId = run.RunId,
                CorrelationId = run.Request.CorrelationId,
                DispatchId = dispatchId,
                Stage = DownloadStage.Metadata,
                Attempt = 1
            };
            await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .BeginStageAttemptAsync(execution, $"job/{run.Request.JobId:N}/run/{run.RunId:N}/metadata/attempt/1");
        }

        private async Task StartHostAsync()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:froststreamdb"] = ConnectionString,
                ["ConnectionStrings:nats"] = _natsContainer.GetConnectionString()
            });

            builder.Services.AddLogging();
            builder.Services.AddSingleton<IClock>(SystemClock.Instance);
            builder.Services.AddDbContext<DataBridgeDbContext>(options =>
            {
                options.UseNpgsql(
                        ConnectionString,
                        npgsqlOptions => npgsqlOptions
                            .UseNodaTime()
                            .MapEnum<LocalStorageProtocol>("local_storage_protocol", "storage")
                            .MapEnum<NetworkStorageProtocol>("network_storage_protocol", "storage")
                            .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider", "storage")
                            .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode", "storage")
                            .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode", "storage")
                            .MapEnum<DownloadJobState>("download_job_state", "downloads")
                            .MapEnum<DownloadJobStatus>("download_job_status", "downloads")
                            .MapEnum<DownloadStage>("download_stage", "downloads")
                            .MapEnum<DownloadStageStatus>("download_stage_status", "downloads")
                            .MapEnum<DownloadGroupKind>("download_group_kind", "downloads")
                            .MapEnum<DownloadGroupStatus>("download_group_status", "downloads")
                            .MapEnum<DownloadArtifactStatus>("download_artifact_status", "downloads")
                            .MapEnum<DownloadWorkerLeaseStatus>("download_worker_lease_status", "downloads")
                            .MapEnum<FailureKind>("failure_kind", "downloads")
                            .MapEnum<IngestOrigin>("ingest_origin", "media"))
                    .UseSnakeCaseNamingConvention();
            });

            builder.Services
                .AddFluentMigratorCore()
                .ConfigureRunner(runner => runner
                    .AddPostgres()
                    .WithGlobalConnectionString(ConnectionString)
                    .ScanIn(typeof(DownloadQueueConsumerService).Assembly).For.Migrations());

            // Provision the production download.> stream so the queue subjects live under JetStream
            // subject capture exactly like the live stack (core request/reply must still work).
            builder.Services.AddNats(options =>
            {
                options.Url = _natsContainer.GetConnectionString();
                options.EnableTopologyProvisioning = true;
            });
            builder.Services.AddNatsTopologySource<DownloadTopology>();

            builder.Services.AddSingleton<IDownloadJobStateNotifier, DownloadJobStateNotifier>();
            builder.Services.AddScoped<IDownloadJobsRepository, DownloadJobsRepository>();
            builder.Services.AddScoped<IDownloadFlowV2Repository, DownloadFlowV2Repository>();
            builder.Services.AddHostedService<DownloadQueueConsumerService>();

            _host = builder.Build();
            using (var scope = _host.Services.CreateScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
                runner.MigrateUp();
            }

            await _host.StartAsync();
            // Give the consumer's core subscriptions a moment to register with the server.
            await Task.Delay(1000);
        }

        private async Task WaitForPostgresAsync()
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    return;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            throw new TimeoutException("PostgreSQL container did not become reachable in time.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                try
                {
                    await _host.StopAsync();
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.Contains("Collection was modified", StringComparison.Ordinal))
                {
                    // SubscriptionBackgroundService mutates its subscription list during shutdown.
                }

                _host.Dispose();
            }

            await _postgresContainer.DisposeAsync();
            await _natsContainer.DisposeAsync();
        }
    }
}
