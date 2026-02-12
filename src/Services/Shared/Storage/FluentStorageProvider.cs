using System.Text.Json;
using FluentStorage;
using FluentStorage.Blobs;
using Shared.Messages;

namespace Shared.Storage;

/// <summary>
/// Builds FluentStorage IBlobStorage instances from storage config responses.
/// </summary>
public static class FluentStorageProvider
{
    public static IBlobStorage CreateStorage(StorageConfigResponse config)
    {
        if (!config.Found || config.Method is null || config.Parameters is null)
            throw new InvalidOperationException($"Storage config not found for key: {config.Key}");

        var connectionString = BuildConnectionString(config.Method.Value, config.Parameters);
        return StorageFactory.Blobs.FromConnectionString(connectionString);
    }

    private static string BuildConnectionString(StorageMethod method, string parametersJson)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        var root = doc.RootElement;

        return method switch
        {
            StorageMethod.PosixLocal => BuildDiskConnectionString(root),
            StorageMethod.StreamingNetwork => BuildNetworkConnectionString(root),
            StorageMethod.ObjectStorage => BuildObjectStorageConnectionString(root),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported storage method")
        };
    }

    private static string BuildDiskConnectionString(JsonElement root)
    {
        var path = root.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("PosixLocal config missing 'path' parameter");

        return $"disk://path={path}";
    }

    private static string BuildNetworkConnectionString(JsonElement root)
    {
        var protocol = root.GetProperty("protocol").GetString()
            ?? throw new InvalidOperationException("StreamingNetwork config missing 'protocol' parameter");

        var host = root.GetProperty("host").GetString()
            ?? throw new InvalidOperationException("StreamingNetwork config missing 'host' parameter");

        var parts = new List<string> { $"host={host}" };

        if (root.TryGetProperty("port", out var portEl))
            parts.Add($"port={portEl.GetInt32()}");

        if (root.TryGetProperty("username", out var userEl) && userEl.ValueKind == JsonValueKind.String)
            parts.Add($"user={userEl.GetString()}");

        if (root.TryGetProperty("password", out var passEl) && passEl.ValueKind == JsonValueKind.String)
            parts.Add($"password={passEl.GetString()}");

        if (root.TryGetProperty("privateKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            parts.Add($"privateKey={keyEl.GetString()}");

        if (root.TryGetProperty("basePath", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
            parts.Add($"path={pathEl.GetString()}");

        // protocol is "ftp", "ftps", or "sftp"
        return $"{protocol}://{string.Join(";", parts)}";
    }

    private static string BuildObjectStorageConnectionString(JsonElement root)
    {
        var provider = root.GetProperty("provider").GetString()
            ?? throw new InvalidOperationException("ObjectStorage config missing 'provider' parameter");

        return provider.ToLowerInvariant() switch
        {
            "s3" => BuildS3ConnectionString(root),
            "azure" => BuildAzureConnectionString(root),
            "gcs" => BuildGcsConnectionString(root),
            _ => throw new ArgumentException($"Unsupported object storage provider: {provider}")
        };
    }

    private static string BuildS3ConnectionString(JsonElement root)
    {
        var parts = new List<string>();

        if (root.TryGetProperty("accessKeyId", out var keyIdEl) && keyIdEl.ValueKind == JsonValueKind.String)
            parts.Add($"keyId={keyIdEl.GetString()}");

        if (root.TryGetProperty("secretKey", out var secretEl) && secretEl.ValueKind == JsonValueKind.String)
            parts.Add($"key={secretEl.GetString()}");

        if (root.TryGetProperty("bucket", out var bucketEl) && bucketEl.ValueKind == JsonValueKind.String)
            parts.Add($"bucket={bucketEl.GetString()}");

        if (root.TryGetProperty("region", out var regionEl) && regionEl.ValueKind == JsonValueKind.String)
            parts.Add($"region={regionEl.GetString()}");

        return $"aws.s3://{string.Join(";", parts)}";
    }

    private static string BuildAzureConnectionString(JsonElement root)
    {
        var parts = new List<string>();

        if (root.TryGetProperty("accountName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            parts.Add($"account={nameEl.GetString()}");

        if (root.TryGetProperty("accountKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            parts.Add($"key={keyEl.GetString()}");

        if (root.TryGetProperty("container", out var containerEl) && containerEl.ValueKind == JsonValueKind.String)
            parts.Add($"container={containerEl.GetString()}");

        return $"azure.blob://{string.Join(";", parts)}";
    }

    private static string BuildGcsConnectionString(JsonElement root)
    {
        var parts = new List<string>();

        if (root.TryGetProperty("projectId", out var projEl) && projEl.ValueKind == JsonValueKind.String)
            parts.Add($"projectId={projEl.GetString()}");

        if (root.TryGetProperty("bucket", out var bucketEl) && bucketEl.ValueKind == JsonValueKind.String)
            parts.Add($"bucket={bucketEl.GetString()}");

        if (root.TryGetProperty("credentialsJson", out var credsEl) && credsEl.ValueKind == JsonValueKind.String)
            parts.Add($"cred={credsEl.GetString()}");

        return $"google.storage://{string.Join(";", parts)}";
    }
}
