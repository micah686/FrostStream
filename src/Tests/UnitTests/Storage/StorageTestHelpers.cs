using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;

namespace UnitTests.Storage;

internal static class StorageTestHelpers
{
    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string Serialize(StorageParametersBase parameters)
    {
        return StorageParametersSerializer.Serialize(GetMethod(parameters), parameters);
    }

    public static string SerializeStored(StorageParametersStoredBase parameters)
    {
        return JsonSerializer.Serialize(parameters, parameters.GetType());
    }

    public static IReadOnlyList<ValidationResult> ValidateObject(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }

    public static StorageMethod GetMethod(StorageParametersBase parameters)
    {
        return parameters switch
        {
            PosixLocalStorageParameters => StorageMethod.Local,
            StreamingNetworkStorageParameters => StorageMethod.Network,
            ObjectStorageParametersBase => StorageMethod.ObjectStorage,
            _ => throw new ArgumentOutOfRangeException(nameof(parameters))
        };
    }

    public static StorageConfigDto CreateDto(StorageMethod method, string key = "storage-key")
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return method switch
        {
            StorageMethod.Local => new StorageConfigDto
            {
                Id = 1,
                Key = key,
                Method = method,
                Description = "desc",
                CreatedAt = now,
                LastUpdated = now,
                Local = new PosixLocalStorageStored
                {
                    Protocol = LocalStorageProtocol.Local,
                    Path = "/mnt/storage"
                }
            },
            StorageMethod.Network => new StorageConfigDto
            {
                Id = 2,
                Key = key,
                Method = method,
                Description = "desc",
                CreatedAt = now,
                LastUpdated = now,
                Network = new StreamingNetworkStorageStored
                {
                    Protocol = NetworkStorageProtocol.Sftp,
                    Host = "example.test",
                    Port = 22,
                    Username = "micah",
                    BasePath = "/upload"
                }
            },
            StorageMethod.ObjectStorage => new StorageConfigDto
            {
                Id = 3,
                Key = key,
                Method = method,
                Description = "desc",
                CreatedAt = now,
                LastUpdated = now,
                ObjectS3Compatible = new S3CompatibleObjectStorageStored
                {
                    Provider = S3CompatibleObjectStorageProvider.AwsS3,
                    BucketName = "bucket",
                    Region = "us-west-2",
                    Endpoint = "https://s3.us-west-2.amazonaws.com",
                    HasSessionToken = true,
                    ForcePathStyle = true,
                    UseSsl = false
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
        };
    }

    public static ServiceProvider BuildDbServices(
        string databaseName,
        ISecretStore secretStore,
        IMessageBus messageBus,
        SaveChangesInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(secretStore);
        services.AddSingleton(messageBus);
        services.AddDbContext<DataBridgeDbContext>(options =>
        {
            options.UseInMemoryDatabase(databaseName);
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });

        return services.BuildServiceProvider();
    }
}

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _store = new(StringComparer.Ordinal);

    public List<string> Writes { get; } = [];
    public List<string> Deletes { get; } = [];

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(path, out var values);
        return Task.FromResult(values);
    }

    public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        Writes.Add(path);
        _store[path] = new Dictionary<string, string>(values, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Deletes.Add(path);
        _store.Remove(path);
        return Task.CompletedTask;
    }
}

internal sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Injected save failure.");
    }
}

internal sealed class FakeMessageBus : IMessageBus
{
    private readonly Dictionary<string, SubscriptionRegistration> _subscriptions = new(StringComparer.Ordinal);

    public List<PublishedMessage> PublishedMessages { get; } = [];
    public IReadOnlyDictionary<string, SubscriptionRegistration> Subscriptions => _subscriptions;

    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
    {
        PublishedMessages.Add(new PublishedMessage(subject, message!));
        return Task.CompletedTask;
    }

    public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
    {
        PublishedMessages.Add(new PublishedMessage(subject, message!));
        return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeAsync<T>(
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
    {
        var subscription = new FakeSubscription();
        _subscriptions[subject] = new SubscriptionRegistration(
            typeof(T),
            queueGroup,
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
    {
        throw new NotSupportedException("Use InvokeAsync in unit tests.");
    }

    public async Task<TResponse?> InvokeAsync<TRequest, TResponse>(string subject, TRequest request)
    {
        var subscription = _subscriptions[subject];
        var response = await subscription.Handler(request!);
        return (TResponse?)response;
    }

    public sealed record PublishedMessage(string Subject, object Message);

    public sealed record SubscriptionRegistration(
        Type MessageType,
        string? QueueGroup,
        FakeSubscription Subscription,
        Func<object, Task<object?>> Handler);
}

internal sealed class FakeSubscription : ISubscription
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMessageContext<T>(string subject, T message) : IMessageContext<T>
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
