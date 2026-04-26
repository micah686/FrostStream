using System.Text.Json;

namespace Shared.Storage;

/// <summary>
/// Splits the secret-bearing input parameter classes into a sensitive-values
/// dictionary (for the secret store) and a non-sensitive stored parameter
/// instance (for the application database and admin DTOs). Also rebuilds the
/// secret-bearing instance by hydrating a stored variant with values fetched
/// back from the secret store.
/// </summary>
public static class StorageSecretSplitter
{
    public const string NetworkPassword = "password";
    public const string NetworkPrivateKey = "privateKey";
    public const string NetworkPublicKey = "publicKey";

    public const string S3AccessKeyId = "accessKeyId";
    public const string S3SecretKeyId = "secretKeyId";
    public const string S3SessionToken = "sessionToken";

    public const string AzureAccountKey = "azureAccountKey";
    public const string AzureConnectionString = "azureConnectionString";
    public const string AzureSasUrl = "azureSasUrl";

    public const string GcpCredentialsJson = "gcpCredentialsJson";
    public const string GcpCredentialsJsonIsBase64Encoded = "gcpCredentialsJsonIsBase64Encoded";

    public static (IReadOnlyDictionary<string, string> Secrets, StorageParametersStoredBase Stored) Split(
        StorageParametersBase input)
    {
        return input switch
        {
            PosixLocalStorageParameters local => SplitLocal(local),
            StreamingNetworkStorageParameters network => SplitNetwork(network),
            S3CompatibleObjectStorageParameters s3 => SplitS3(s3),
            AzureBlobObjectStorageParameters azure => SplitAzure(azure),
            GoogleCloudStorageObjectStorageParameters gcs => SplitGcs(gcs),
            _ => throw new ArgumentException($"Unsupported parameters type: {input.GetType().Name}", nameof(input))
        };
    }

    private static (IReadOnlyDictionary<string, string>, StorageParametersStoredBase) SplitLocal(
        PosixLocalStorageParameters input)
    {
        return (
            new Dictionary<string, string>(StringComparer.Ordinal),
            new PosixLocalStorageStored
            {
                Protocol = input.Protocol,
                Path = input.Path
            });
    }

    private static (IReadOnlyDictionary<string, string>, StorageParametersStoredBase) SplitNetwork(
        StreamingNetworkStorageParameters input)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(secrets, NetworkPassword, input.Password);
        AddIfPresent(secrets, NetworkPrivateKey, input.PrivateKey);
        AddIfPresent(secrets, NetworkPublicKey, input.PublicKey);

        var stored = new StreamingNetworkStorageStored
        {
            Protocol = input.Protocol,
            Host = input.Host,
            Port = input.Port,
            Username = input.Username,
            BasePath = input.BasePath
        };
        return (secrets, stored);
    }

    private static (IReadOnlyDictionary<string, string>, StorageParametersStoredBase) SplitS3(
        S3CompatibleObjectStorageParameters input)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [S3AccessKeyId] = input.AccessKeyId,
            [S3SecretKeyId] = input.SecretKeyId
        };
        AddIfPresent(secrets, S3SessionToken, input.SessionTokenSecretId);

        var stored = new S3CompatibleObjectStorageStored
        {
            Provider = input.Provider,
            BucketName = input.BucketName,
            Region = input.Region,
            Endpoint = input.Endpoint,
            HasSessionToken = !string.IsNullOrWhiteSpace(input.SessionTokenSecretId),
            ForcePathStyle = input.ForcePathStyle,
            UseSsl = input.UseSsl
        };
        return (secrets, stored);
    }

    private static (IReadOnlyDictionary<string, string>, StorageParametersStoredBase) SplitAzure(
        AzureBlobObjectStorageParameters input)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(secrets, AzureAccountKey, input.AzureAccountKeySecretId);
        AddIfPresent(secrets, AzureConnectionString, input.AzureConnectionStringSecretId);
        AddIfPresent(secrets, AzureSasUrl, input.AzureSasUrlSecretId);

        var stored = new AzureBlobObjectStorageStored
        {
            CredentialMode = input.CredentialMode,
            ContainerName = input.ContainerName,
            AzureAccountName = input.AzureAccountName
        };
        return (secrets, stored);
    }

    private static (IReadOnlyDictionary<string, string>, StorageParametersStoredBase) SplitGcs(
        GoogleCloudStorageObjectStorageParameters input)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input.GcpCredentialsJson is not null)
        {
            secrets[GcpCredentialsJson] = input.GcpCredentialsJson.Value.GetRawText();
            secrets[GcpCredentialsJsonIsBase64Encoded] = input.GcpCredentialsJsonIsBase64Encoded ? "true" : "false";
        }

        var stored = new GoogleCloudStorageObjectStorageStored
        {
            BucketName = input.BucketName,
            CredentialMode = input.CredentialMode,
            GcpCredentialsFilePath = input.GcpCredentialsFilePath,
            GcpProjectId = input.GcpProjectId
        };
        return (secrets, stored);
    }

    public static StorageParametersBase Hydrate(
        StorageParametersStoredBase stored,
        IReadOnlyDictionary<string, string>? secrets)
    {
        return stored switch
        {
            PosixLocalStorageStored local => new PosixLocalStorageParameters
            {
                Protocol = local.Protocol,
                Path = local.Path
            },
            StreamingNetworkStorageStored network => HydrateNetwork(network, secrets),
            S3CompatibleObjectStorageStored s3 => HydrateS3(s3, secrets),
            AzureBlobObjectStorageStored azure => HydrateAzure(azure, secrets),
            GoogleCloudStorageObjectStorageStored gcs => HydrateGcs(gcs, secrets),
            _ => throw new ArgumentException($"Unsupported stored type: {stored.GetType().Name}", nameof(stored))
        };
    }

    private static StreamingNetworkStorageParameters HydrateNetwork(
        StreamingNetworkStorageStored stored,
        IReadOnlyDictionary<string, string>? secrets)
    {
        secrets ??= new Dictionary<string, string>();
        return new StreamingNetworkStorageParameters
        {
            Protocol = stored.Protocol,
            Host = stored.Host,
            Port = stored.Port,
            Username = stored.Username,
            BasePath = stored.BasePath,
            Password = TryGet(secrets, NetworkPassword),
            PrivateKey = TryGet(secrets, NetworkPrivateKey),
            PublicKey = TryGet(secrets, NetworkPublicKey)
        };
    }

    private static S3CompatibleObjectStorageParameters HydrateS3(
        S3CompatibleObjectStorageStored stored,
        IReadOnlyDictionary<string, string>? secrets)
    {
        secrets ??= new Dictionary<string, string>();
        return new S3CompatibleObjectStorageParameters
        {
            Provider = stored.Provider,
            BucketName = stored.BucketName,
            Region = stored.Region,
            Endpoint = stored.Endpoint,
            AccessKeyId = TryGet(secrets, S3AccessKeyId)
                ?? throw new InvalidOperationException("S3 access key missing from secret store."),
            SecretKeyId = TryGet(secrets, S3SecretKeyId)
                ?? throw new InvalidOperationException("S3 secret key missing from secret store."),
            SessionTokenSecretId = TryGet(secrets, S3SessionToken),
            ForcePathStyle = stored.ForcePathStyle,
            UseSsl = stored.UseSsl
        };
    }

    private static AzureBlobObjectStorageParameters HydrateAzure(
        AzureBlobObjectStorageStored stored,
        IReadOnlyDictionary<string, string>? secrets)
    {
        secrets ??= new Dictionary<string, string>();
        return new AzureBlobObjectStorageParameters
        {
            CredentialMode = stored.CredentialMode,
            ContainerName = stored.ContainerName,
            AzureAccountName = stored.AzureAccountName,
            AzureAccountKeySecretId = TryGet(secrets, AzureAccountKey),
            AzureConnectionStringSecretId = TryGet(secrets, AzureConnectionString),
            AzureSasUrlSecretId = TryGet(secrets, AzureSasUrl)
        };
    }

    private static GoogleCloudStorageObjectStorageParameters HydrateGcs(
        GoogleCloudStorageObjectStorageStored stored,
        IReadOnlyDictionary<string, string>? secrets)
    {
        secrets ??= new Dictionary<string, string>();
        JsonElement? json = null;
        if (secrets.TryGetValue(GcpCredentialsJson, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            using var doc = JsonDocument.Parse(raw);
            json = doc.RootElement.Clone();
        }

        var isBase64 = false;
        if (secrets.TryGetValue(GcpCredentialsJsonIsBase64Encoded, out var b64) &&
            bool.TryParse(b64, out var parsed))
        {
            isBase64 = parsed;
        }

        return new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = stored.BucketName,
            CredentialMode = stored.CredentialMode,
            GcpCredentialsJson = json,
            GcpCredentialsJsonIsBase64Encoded = isBase64,
            GcpCredentialsFilePath = stored.GcpCredentialsFilePath,
            GcpProjectId = stored.GcpProjectId
        };
    }

    private static void AddIfPresent(Dictionary<string, string> bag, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            bag[key] = value;
        }
    }

    private static string? TryGet(IReadOnlyDictionary<string, string> dict, string key)
    {
        return dict.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}
