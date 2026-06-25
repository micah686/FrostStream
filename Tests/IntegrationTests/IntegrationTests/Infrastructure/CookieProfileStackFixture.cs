using DataBridge.Data;
using DataBridge.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using FlySwattr.NATS.Abstractions;
using FlySwattr.NATS.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Database;
using Shared.Secrets;
using Shared.Storage;
using Testcontainers.Nats;

namespace IntegrationTests.Infrastructure;

/// <summary>
/// Real Postgres + NATS + OpenBAO stack for end-to-end verification of the cookie-profile feature:
/// migration 027 (auth.cookie_profiles), the NATS cookie-profile request/reply flow handled by
/// <see cref="CookieProfileConsumerService"/>, and user-scoped OpenBAO read/write through
/// <see cref="OpenBaoSecretStore"/>.
/// </summary>
public sealed class CookieProfileStackFixture : IAsyncDisposable
{
    private const string OpenBaoToken = "froststream-test-root";

    private readonly NatsContainer _natsContainer = new NatsBuilder()
        .WithImage("nats:2.10")
        .WithCommand("--jetstream")
        .Build();

    private readonly IContainer _postgresContainer = new ContainerBuilder()
        .WithImage("postgres:17")
        .WithEnvironment("POSTGRES_DB", "froststream_cookie_tests")
        .WithEnvironment("POSTGRES_USER", "postgres")
        .WithEnvironment("POSTGRES_PASSWORD", "postgres")
        .WithPortBinding(5432, true)
        .Build();

    private readonly IContainer _openBaoContainer = new ContainerBuilder()
        .WithImage("openbao/openbao:latest")
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", OpenBaoToken)
        .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
        .WithPortBinding(8200, true)
        .WithCommand("server", "-dev", "-dev-root-token-id", OpenBaoToken, "-dev-listen-address", "0.0.0.0:8200")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8200))
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IHost? _dataBridgeHost;
    private bool _initialized;

    public IMessageBus Bus => _dataBridgeHost!.Services.GetRequiredService<IMessageBus>();

    public string OpenBaoAddress => $"http://127.0.0.1:{_openBaoContainer.GetMappedPublicPort(8200)}";

    private string PostgresConnectionString =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _postgresContainer.Hostname,
            Port = _postgresContainer.GetMappedPublicPort(5432),
            Database = "froststream_cookie_tests",
            Username = "postgres",
            Password = "postgres",
            SearchPath = "storage,downloads,media,maintenance,metadata,auth,public"
        }.ConnectionString;

    private string NatsUrl => _natsContainer.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync();
        try
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
                    await _openBaoContainer.StartAsync();
                    break;
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(1000);
                }
            }

            await WaitForPostgresAsync();
            await WaitForOpenBaoAsync();
            await StartDataBridgeAsync();
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A real OpenBAO-backed secret store pointed at the test container (KV v2 at <c>secret/</c>).</summary>
    public OpenBaoSecretStore CreateSecretStore()
        => new(Options.Create(new OpenBaoOptions { Address = OpenBaoAddress, Token = OpenBaoToken }));

    public async Task<CookieProfileEntity?> FindProfileAsync(string ownerSubject, string profileKey)
    {
        await using var scope = _dataBridgeHost!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        return await db.CookieProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerSubject == ownerSubject && x.ProfileKey == profileKey);
    }

    public async Task<bool> CookieProfilesTableExistsAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('auth.cookie_profiles') IS NOT NULL;";
        var result = await command.ExecuteScalarAsync();
        return result is true;
    }

    private async Task StartDataBridgeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:froststreamdb"] = PostgresConnectionString,
            ["ConnectionStrings:nats"] = NatsUrl
        });

        builder.Services.AddLogging();
        builder.Services.AddSingleton<NodaTime.IClock>(NodaTime.SystemClock.Instance);
        builder.Services.AddDbContext<DataBridgeDbContext>(options =>
        {
            options.UseNpgsql(
                    PostgresConnectionString,
                    npgsqlOptions => npgsqlOptions
                        .UseNodaTime()
                        .MapEnum<LocalStorageProtocol>("local_storage_protocol", "storage")
                        .MapEnum<NetworkStorageProtocol>("network_storage_protocol", "storage")
                        .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider", "storage")
                        .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode", "storage")
                        .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode", "storage"))
                .UseSnakeCaseNamingConvention();
        });

        builder.Services
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(PostgresConnectionString)
                .ScanIn(typeof(CookieProfileConsumerService).Assembly).For.Migrations());

        builder.Services.AddEnterpriseNATSMessaging(options =>
        {
            options.Core.Url = NatsUrl;
            options.EnableTopologyProvisioning = false;
            options.EnablePayloadOffloading = false;
            options.EnableResilience = false;
            options.EnableCaching = false;
            options.EnableDistributedLock = false;
            options.EnableDlqAdvisoryListener = false;
        });

        builder.Services.AddHostedService<CookieProfileConsumerService>();

        _dataBridgeHost = builder.Build();
        using (var scope = _dataBridgeHost.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        await _dataBridgeHost.StartAsync();
        await Task.Delay(1000);
    }

    private async Task WaitForPostgresAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(PostgresConnectionString);
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

    private async Task WaitForOpenBaoAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(OpenBaoAddress) };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var response = await http.GetAsync("/v1/sys/health");
                if ((int)response.StatusCode is >= 200 and < 500)
                {
                    return;
                }
            }
            catch
            {
                // not ready yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("OpenBAO container did not become reachable in time.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataBridgeHost is not null)
        {
            try
            {
                await _dataBridgeHost.StopAsync();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("Collection was modified", StringComparison.Ordinal))
            {
                // SubscriptionBackgroundService mutates its subscription list during shutdown.
            }

            _dataBridgeHost.Dispose();
        }

        await _openBaoContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
        await _natsContainer.DisposeAsync();
    }
}
