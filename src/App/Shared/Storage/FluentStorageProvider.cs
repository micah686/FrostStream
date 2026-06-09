using FluentStorage;
using FluentStorage.Blobs;
using System.Text;
using System.Text.Json;

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
            StorageMethod.Local => BuildDiskConnectionString((PosixLocalStorageParameters)parameters),
            StorageMethod.Network => BuildNetworkConnectionString((StreamingNetworkStorageParameters)parameters),
            StorageMethod.ObjectStorage => BuildObjectStorageConnectionString((ObjectStorageParametersBase)parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported storage method")
        };
    }

    private static string BuildDiskConnectionString(PosixLocalStorageParameters parameters)
    {
        return $"disk://path={LocalStoragePathResolver.Resolve(parameters.Path)}";
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

    private static string BuildObjectStorageConnectionString(ObjectStorageParametersBase parameters)
    {
        return parameters switch
        {
            S3CompatibleObjectStorageParameters s3 => BuildS3ConnectionString(s3),
            AzureBlobObjectStorageParameters azure => BuildAzureConnectionString(azure),
            GoogleCloudStorageObjectStorageParameters gcs => BuildGcsConnectionString(gcs),
            _ => throw new ArgumentOutOfRangeException(nameof(parameters), parameters.GetType().Name, "Unsupported object storage provider")
        };
    }

    private static string BuildS3ConnectionString(S3CompatibleObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "bucket", parameters.BucketName);
        AddIfPresent(parts, "region", parameters.Region);
        AddIfPresent(parts, "keyId", parameters.AccessKeyId);
        AddIfPresent(parts, "key", parameters.SecretKeyId);
        AddIfPresent(parts, "serviceUrl", parameters.Endpoint);

        if (parameters.ForcePathStyle)
        {
            parts.Add("forcePathStyle=true");
        }

        if (parameters.UseSsl is not null)
        {
            parts.Add($"useSsl={parameters.UseSsl.Value.ToString().ToLowerInvariant()}");
        }

        return $"aws.s3://{string.Join(";", parts)}";
    }

    private static string BuildAzureConnectionString(AzureBlobObjectStorageParameters parameters)
    {
        return parameters.CredentialMode switch
        {
            AzureBlobCredentialMode.AccountKey => BuildAzureAccountKeyConnectionString(parameters),
            AzureBlobCredentialMode.ConnectionString => BuildAzureConnectionStringFromSecret(parameters),
            AzureBlobCredentialMode.SasUrl => BuildAzureSasConnectionString(parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(parameters.CredentialMode), parameters.CredentialMode, "Unsupported Azure Blob credential mode")
        };
    }

    private static string BuildAzureAccountKeyConnectionString(AzureBlobObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "container", parameters.ContainerName);
        AddIfPresent(parts, "account", parameters.AzureAccountName);
        AddIfPresent(parts, "key", parameters.AzureAccountKeySecretId);

        return $"azure.blob://{string.Join(";", parts)}";
    }

    private static string BuildAzureConnectionStringFromSecret(AzureBlobObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "container", parameters.ContainerName);
        AddIfPresent(parts, "connectionString", parameters.AzureConnectionStringSecretId);

        return $"azure.blob://{string.Join(";", parts)}";
    }

    private static string BuildAzureSasConnectionString(AzureBlobObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "container", parameters.ContainerName);
        AddIfPresent(parts, "sasUrl", parameters.AzureSasUrlSecretId);

        return $"azure.blob://{string.Join(";", parts)}";
    }

    private static string BuildGcsConnectionString(GoogleCloudStorageObjectStorageParameters parameters)
    {
        var parts = new List<string>();

        AddIfPresent(parts, "bucket", parameters.BucketName);
        AddIfPresent(parts, "projectId", parameters.GcpProjectId);

        switch (parameters.CredentialMode)
        {
            case GoogleCloudStorageCredentialMode.CredentialsJson:
                if (parameters.GcpCredentialsJson is not null)
                {
                    parts.Add($"cred={GetGoogleCredentialsJson(parameters)}");
                }
                break;

            case GoogleCloudStorageCredentialMode.CredentialsFilePath:
                AddIfPresent(parts, "credFile", parameters.GcpCredentialsFilePath);
                break;

            case GoogleCloudStorageCredentialMode.WorkloadIdentity:
                parts.Add("auth=workloadIdentity");
                break;

            case GoogleCloudStorageCredentialMode.DefaultCredentials:
                parts.Add("auth=defaultCredentials");
                break;
        }

        return $"google.storage://{string.Join(";", parts)}";
    }

    private static string GetGoogleCredentialsJson(GoogleCloudStorageObjectStorageParameters parameters)
    {
        if (parameters.GcpCredentialsJson is null)
        {
            throw new InvalidOperationException("GCP credentials JSON is required.");
        }

        if (parameters.GcpCredentialsJsonIsBase64Encoded)
        {
            var encoded = parameters.GcpCredentialsJson.Value.ValueKind == JsonValueKind.String
                ? parameters.GcpCredentialsJson.Value.GetString()
                : parameters.GcpCredentialsJson.Value.GetRawText();

            if (string.IsNullOrWhiteSpace(encoded))
            {
                throw new InvalidOperationException("Base64-encoded GCP credentials JSON is empty.");
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }

        return parameters.GcpCredentialsJson.Value.GetRawText();
    }

    private static void AddIfPresent(ICollection<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={value}");
        }
    }
}
