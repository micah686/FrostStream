using DataBridge.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Npgsql;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace IntegrationTests.DataBridge;

public sealed class WatchStateConsumerServiceTests
{
    private static readonly PostgresFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static WatchStateConsumerServiceTests()
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
    public async Task Upsert_Can_Mark_Unwatched_For_One_User_Without_Clearing_Another_User()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid);

        var bus = new FakeMessageBus();
        var service = Fixture.CreateService(bus);
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus, expected: 2);

        try
        {
            var firstUserWatched = await UpsertAsync(bus, "reader-a", mediaGuid, completed: true);
            var secondUserWatched = await UpsertAsync(bus, "reader-b", mediaGuid, completed: true);

            firstUserWatched.Success.ShouldBeTrue();
            firstUserWatched.State!.Completed.ShouldBeTrue();
            firstUserWatched.State.WatchedAt.ShouldNotBeNull();
            secondUserWatched.Success.ShouldBeTrue();
            secondUserWatched.State!.Completed.ShouldBeTrue();

            var firstUserUnwatched = await UpsertAsync(bus, "reader-a", mediaGuid, completed: false);
            firstUserUnwatched.Success.ShouldBeTrue();
            firstUserUnwatched.State!.Completed.ShouldBeFalse();
            firstUserUnwatched.State.WatchedAt.ShouldBeNull();

            var firstUserState = await GetAsync(bus, "reader-a", mediaGuid);
            var secondUserState = await GetAsync(bus, "reader-b", mediaGuid);

            firstUserState.Success.ShouldBeTrue();
            firstUserState.State!.Completed.ShouldBeFalse();
            firstUserState.State.WatchedAt.ShouldBeNull();
            secondUserState.Success.ShouldBeTrue();
            secondUserState.State!.Completed.ShouldBeTrue();
            secondUserState.State.WatchedAt.ShouldNotBeNull();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static Task<WatchStateResponse> UpsertAsync(
        FakeMessageBus bus,
        string ownerSubject,
        Guid mediaGuid,
        bool completed)
        => bus.InvokeAsync<WatchStateUpsertRequest, WatchStateResponse>(
            WatchStateSubjects.Upsert,
            new WatchStateUpsertRequest
            {
                OwnerSubject = ownerSubject,
                MediaGuid = mediaGuid,
                PositionSeconds = completed ? 200 : 0,
                DurationSeconds = 200,
                Completed = completed
            });

    private static Task<WatchStateResponse> GetAsync(FakeMessageBus bus, string ownerSubject, Guid mediaGuid)
        => bus.InvokeAsync<WatchStateGetRequest, WatchStateResponse>(
            WatchStateSubjects.Get,
            new WatchStateGetRequest
            {
                OwnerSubject = ownerSubject,
                MediaGuid = mediaGuid
            });

    private static async Task WaitForSubscriptionsAsync(FakeMessageBus bus, int expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (bus.Subscriptions.Count == expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Consumer did not register subscriptions in time.");
    }

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private readonly IContainer _postgresContainer = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_DB", "froststream_watch_state_tests")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithPortBinding(5432, true)
            .Build();

        private NpgsqlDataSource? _dataSource;
        private bool _initialized;

        public Instant Now { get; } = Instant.FromUtc(2026, 6, 4, 12, 0);

        private string ConnectionString =>
            new NpgsqlConnectionStringBuilder
            {
                Host = _postgresContainer.Hostname,
                Port = _postgresContainer.GetMappedPublicPort(5432),
                Database = "froststream_watch_state_tests",
                Username = "postgres",
                Password = "postgres",
                SearchPath = "storage,downloads,media,maintenance,metadata,auth,public"
            }.ConnectionString;

        private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

        public WatchStateConsumerService CreateService(FakeMessageBus bus)
            => new(bus, DataSource, new FixedClock(Now), NullLogger<WatchStateConsumerService>.Instance);

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            await _postgresContainer.StartAsync();
            await WaitForPostgresAsync();
            await RunMigrationsAsync();

            _dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
            _initialized = true;
        }

        public async Task ResetAsync()
        {
            await using var command = DataSource.CreateCommand("TRUNCATE TABLE media RESTART IDENTITY CASCADE;");
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedMediaAsync(Guid mediaGuid)
        {
            await using var command = DataSource.CreateCommand(
                "INSERT INTO media (media_guid, created_at) VALUES (@media_guid, @created_at);");
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            command.Parameters.AddWithValue("created_at", Now.ToDateTimeOffset());
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_dataSource is not null)
            {
                await _dataSource.DisposeAsync();
            }

            await _postgresContainer.DisposeAsync();
        }

        private async Task RunMigrationsAsync()
        {
            var services = new ServiceCollection();
            services
                .AddFluentMigratorCore()
                .ConfigureRunner(runner => runner
                    .AddPostgres()
                    .WithGlobalConnectionString(ConnectionString)
                    .ScanIn(typeof(WatchStateConsumerService).Assembly).For.Migrations());

            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        private async Task WaitForPostgresAsync()
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    await connection.CloseAsync();
                    return;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            throw new TimeoutException("PostgreSQL container did not become reachable in time.");
        }
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class FakeMessageBus : IMessageBus
    {
        private readonly Dictionary<string, SubscriptionRegistration> _subscriptions = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, SubscriptionRegistration> Subscriptions => _subscriptions;

        public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ISubscription> SubscribeAsync<T>(
            string subject,
            Func<IMessageContext<T>, Task> handler,
            string? queueGroup = null,
            CancellationToken cancellationToken = default)
        {
            var subscription = new FakeSubscription();
            _subscriptions[subject] = new SubscriptionRegistration(
                subscription,
                async message =>
                {
                    var context = new FakeMessageContext<T>(subject, (T)message);
                    await handler(context);
                    return context.Response;
                });

            return Task.FromResult<ISubscription>(subscription);
        }

        public Task<TResponse?> RequestAsync<TRequest, TResponse>(
            string subject,
            TRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use InvokeAsync in tests.");

        public async Task<TResponse> InvokeAsync<TRequest, TResponse>(string subject, TRequest request)
        {
            var subscription = _subscriptions[subject];
            var response = await subscription.Handler(request!);
            return (TResponse)response!;
        }

        public sealed record SubscriptionRegistration(
            FakeSubscription Subscription,
            Func<object, Task<object?>> Handler);
    }

    private sealed class FakeSubscription : ISubscription
    {
        public Guid Id { get; } = Guid.NewGuid();

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMessageContext<T>(string subject, T message) : IMessageContext<T>
    {
        public T Message { get; } = message;
        public string Subject { get; } = subject;
        public MessageHeaders Headers { get; } = MessageHeaders.Empty;
        public string? ReplyTo { get; } = null;
        public object? Response { get; private set; }

        public Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default)
        {
            Response = response;
            return Task.CompletedTask;
        }
    }
}
