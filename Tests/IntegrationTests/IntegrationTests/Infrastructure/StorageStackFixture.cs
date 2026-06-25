using DataBridge.Data;
using DataBridge.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using FlySwattr.NATS.Abstractions;
using FlySwattr.NATS.Core;
using FlySwattr.NATS.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.Serializers.Json;
using Npgsql;
using NodaTime.Serialization.SystemTextJson;
using Shared.Database;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testcontainers.Nats;
using Testcontainers.PostgreSql;

namespace IntegrationTests.Infrastructure;

public sealed class StorageStackFixture(SaveChangesInterceptor? interceptor = null) : IAsyncDisposable
{
    private readonly NatsContainer _natsContainer = new NatsBuilder()
        .WithImage("nats:2.10")
        .WithCommand("--jetstream")
        .Build();

    private readonly IContainer _postgresContainer = new ContainerBuilder()
        .WithImage("postgres:17")
        .WithEnvironment("POSTGRES_DB", "froststream_storage_tests")
        .WithEnvironment("POSTGRES_USER", "postgres")
        .WithEnvironment("POSTGRES_PASSWORD", "postgres")
        .WithPortBinding(5432, true)
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SaveChangesInterceptor? _interceptor = interceptor;

    private IHost? _dataBridgeHost;
    private StorageWebApplicationFactory? _webAppFactory;
    private HttpClient? _client;
    private bool _initialized;

    public InMemorySecretStore SecretStore { get; } = new();

    public HttpClient Client => _client ?? throw new InvalidOperationException("Fixture not initialized.");
    public IMessageBus DataBridgeBus => _dataBridgeHost!.Services.GetRequiredService<IMessageBus>();
    public string NatsUrl => _natsContainer.GetConnectionString();
    public string PostgresConnectionString =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _postgresContainer.Hostname,
            Port = _postgresContainer.GetMappedPublicPort(5432),
            Database = "froststream_storage_tests",
            Username = "postgres",
            Password = "postgres",
            SearchPath = "storage,downloads,media,maintenance,metadata,auth,public"
        }.ConnectionString;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

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

            await _natsContainer.StartAsync();
            await _postgresContainer.StartAsync();
            await WaitForPostgresAsync();

            await StartDataBridgeAsync();

            _webAppFactory = new StorageWebApplicationFactory(NatsUrl, SecretStore);
            _client = _webAppFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync()
    {
        await InitializeAsync();
        await StartDataBridgeAsync();
        await using var scope = _dataBridgeHost!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM storage_keys WHERE key <> 'default';");
        SecretStore.Clear();
    }

    public async Task StartDataBridgeAsync()
    {
        if (_dataBridgeHost is not null)
        {
            return;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:froststreamdb"] = PostgresConnectionString,
            ["ConnectionStrings:nats"] = NatsUrl
        });

        builder.Services.AddLogging();
        builder.Services.AddSingleton<ISecretStore>(SecretStore);
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

            if (_interceptor is not null)
            {
                options.AddInterceptors(_interceptor);
            }
        });

        builder.Services
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(PostgresConnectionString)
                .ScanIn(typeof(StorageCrudConsumerService).Assembly).For.Migrations());

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

        builder.Services.AddHostedService<StorageCrudConsumerService>();

        _dataBridgeHost = builder.Build();
        using (var scope = _dataBridgeHost.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        await _dataBridgeHost.StartAsync();
        await Task.Delay(1000);
    }

    public async Task StopDataBridgeAsync()
    {
        if (_dataBridgeHost is null)
        {
            return;
        }

        try
        {
            await _dataBridgeHost.StopAsync();
        }
        catch (InvalidOperationException) when (_dataBridgeHost is not null)
        {
            // StorageCrudConsumerService.StopAsync currently mutates its subscription list while iterating.
            // For transport-failure tests we only need the host torn down, not a graceful subscription stop.
        }

        _dataBridgeHost.Dispose();
        _dataBridgeHost = null;
    }

    public async Task<RealMessageBusHarness> CreateExternalBusAsync()
    {
        var opts = new NatsOpts
        {
            Url = NatsUrl,
            SerializerRegistry = NatsJsonSerializerRegistry.Default
        };
        var connection = new NatsConnection(opts);
        await connection.ConnectAsync();
        return new RealMessageBusHarness(connection, new NatsMessageBus(connection, NullLogger<NatsMessageBus>.Instance));
    }

    public async Task<StorageConfigEntity?> FindStorageAsync(string key)
    {
        await using var scope = _dataBridgeHost!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        return await db.StorageConfigs
            .AsNoTracking()
            .Include(x => x.Local)
            .Include(x => x.Network)
            .Include(x => x.ObjectS3Compatible)
            .Include(x => x.ObjectAzureBlob)
            .Include(x => x.ObjectGoogleCloudStorage)
            .SingleOrDefaultAsync(x => x.Key == key);
    }

    public async Task<WebAPI.Features.Storage.Models.LocalStorageConfigResponse> CreateLocalAsync(string key, string path)
    {
        var response = await Client.PostAsJsonAsync("/api/storage/local/create", new WebAPI.Features.Storage.Models.LocalStorageUpsertRequest
        {
            Key = key,
            Description = "desc",
            Protocol = LocalStorageProtocol.Local,
            Path = path
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebAPI.Features.Storage.Models.LocalStorageConfigResponse>(JsonOptions))!;
    }

    public async Task<HttpResponseMessage> CreateNetworkAsync(WebAPI.Features.Storage.Models.NetworkStorageUpsertRequest request)
    {
        return await Client.PostAsJsonAsync("/api/storage/network/create", request);
    }

    public async Task<HttpResponseMessage> CreateS3Async(WebAPI.Features.Storage.Models.S3CompatibleObjectStorageUpsertRequest request)
    {
        return await Client.PostAsJsonAsync("/api/storage/object/s3-compatible/create", request);
    }

    public async Task<HttpResponseMessage> CreateAzureAsync(WebAPI.Features.Storage.Models.AzureBlobObjectStorageUpsertRequest request)
    {
        return await Client.PostAsJsonAsync("/api/storage/object/azure-blob/create", request);
    }

    public async Task<HttpResponseMessage> CreateGcsAsync(WebAPI.Features.Storage.Models.GoogleCloudStorageObjectStorageUpsertRequest request)
    {
        return await Client.PostAsJsonAsync("/api/storage/object/google-cloud-storage/create", request);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
        }

        if (_webAppFactory is not null)
        {
            try
            {
                await _webAppFactory.DisposeAsync();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("Collection was modified", StringComparison.Ordinal))
            {
                // SubscriptionBackgroundService currently mutates its subscription list during shutdown.
            }
        }

        if (_dataBridgeHost is not null)
        {
            try
            {
                await _dataBridgeHost.StopAsync();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("Collection was modified", StringComparison.Ordinal))
            {
                // SubscriptionBackgroundService currently mutates its subscription list during shutdown.
            }

            _dataBridgeHost.Dispose();
        }

        await _postgresContainer.DisposeAsync();
        await _natsContainer.DisposeAsync();
        _gate.Dispose();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private async Task WaitForPostgresAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(PostgresConnectionString);
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

    private sealed class StorageWebApplicationFactory(
        string natsUrl,
        ISecretStore secretStore) : WebApplicationFactory<WebAPI.Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:nats", natsUrl);
            builder.UseSetting("NATS:Url", natsUrl);
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:nats"] = natsUrl,
                    ["NATS:Url"] = natsUrl
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecretStore>();
                services.AddSingleton(secretStore);
            });
        }
    }
}

public sealed class RealMessageBusHarness(NatsConnection connection, IMessageBus bus) : IAsyncDisposable
{
    public IMessageBus Bus { get; } = bus;

    public async ValueTask DisposeAsync()
    {
        if (Bus is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}

public sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Injected save failure.");
    }
}
