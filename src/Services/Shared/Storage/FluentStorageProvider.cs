using FluentStorage;
using FluentStorage.Blobs;

namespace Shared.Storage;

/// <summary>
/// Builds FluentStorage <see cref="IBlobStorage"/> instances from storage config responses.
/// </summary>
public static class FluentStorageProvider
{
    public static IBlobStorage CreateStorage(StorageConfigResponse config)
    {
        if (!config.Found || config.Method is null || string.IsNullOrWhiteSpace(config.Parameters))
        {
            throw new InvalidOperationException(
                $"Storage config not found for key: {config.Key ?? "<unknown>"}");
        }

        var connectionString = BuildConnectionString(config.Method.Value, config.Parameters);
        return StorageFactory.Blobs.FromConnectionString(connectionString);
    }

    private static string BuildConnectionString(StorageMethod method, string parametersJson)
    {
        if (!StorageParametersSerializer.TryDeserialize(method, parametersJson, out var parameters, out var error) ||
            parameters is null)
        {
            throw new InvalidOperationException(
                $"Invalid storage parameters for method '{method}': {error ?? "unable to parse parameters"}");
        }

        return method switch
        {
            StorageMethod.PosixLocal => BuildDiskConnectionString((PosixLocalStorageParameters)parameters),
            StorageMethod.StreamingNetwork => BuildNetworkConnectionString((StreamingNetworkStorageParameters)parameters),
            StorageMethod.ObjectStorage => BuildObjectStorageConnectionString((ObjectStorageParameters)parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported storage method")
        };
    }

    private static string BuildDiskConnectionString(PosixLocalStorageParameters parameters)
    {
        return $"disk://path={parameters.Path}";
    }

    private static string BuildNetworkConnectionString(StreamingNetworkStorageParameters parameters)
    {
        var protocol = parameters.Protocol.ToString().ToLowerInvariant();
        var parts = new List<string> { $"host={parameters.Host}" };

        if (parameters.Port is not null)
        {
            parts.Add($"port={parameters.Port.Value}");
        }

        AddIfPresent(parts, "user", parameters.Username);
        AddIfPresent(parts, "password", parameters.Password);
        AddIfPresent(parts, "privateKey", parameters.PrivateKey);
        AddIfPresent(parts, "publicKey", parameters.PublicKey);
        AddIfPresent(parts, "path", parameters.BasePath);

        return $"{protocol}://{string.Join(";", parts)}";
    }

    private static string BuildObjectStorageConnectionString(ObjectStorageParameters parameters)
    {
        return parameters.Provider switch
        {
            ObjectStorageProtocol.S3 => BuildS3ConnectionString(parameters),
            ObjectStorageProtocol.MinIo => BuildS3ConnectionString(parameters),
            ObjectStorageProtocol.AzureBlob => BuildAzureConnectionString(parameters),
            ObjectStorageProtocol.Gcs => BuildGcsConnectionString(parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(parameters.Provider), parameters.Provider, "Unsupported object storage provider")
        };
    }

    private static string BuildS3ConnectionString(ObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "bucket", parameters.Container);
        AddIfPresent(parts, "region", parameters.Region);
        AddIfPresent(parts, "keyId", parameters.AccessKeyId);
        AddIfPresent(parts, "key", parameters.SecretKey);
        AddIfPresent(parts, "serviceUrl", parameters.Endpoint);
        AddIfPresent(parts, "path", parameters.BasePath);

        return $"aws.s3://{string.Join(";", parts)}";
    }

    private static string BuildAzureConnectionString(ObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "container", parameters.Container);
        AddIfPresent(parts, "path", parameters.BasePath);
        AddIfPresent(parts, "endpoint", parameters.Endpoint);

        if (!parameters.UseDefaultCredentials)
        {
            AddIfPresent(parts, "account", parameters.AccessKeyId);
            AddIfPresent(parts, "key", parameters.SecretKey);
        }

        return $"azure.blob://{string.Join(";", parts)}";
    }

    private static string BuildGcsConnectionString(ObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "bucket", parameters.Container);
        AddIfPresent(parts, "path", parameters.BasePath);

        if (!parameters.UseDefaultCredentials)
        {
            AddIfPresent(parts, "cred", parameters.SecretKey);
        }

        return $"google.storage://{string.Join(";", parts)}";
    }

    private static void AddIfPresent(ICollection<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={value}");
        }
    }
}
