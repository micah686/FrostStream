using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Azure.Storage.Blobs;
using FluentFTP;
using FluentStorage;
using FluentStorage.FTP.Storage;
using FluentStorage.Storage;
using Renci.SshNet;

namespace Shared.Storage;

/// <summary>
/// Builds FluentStorage v8 <see cref="IStore"/> instances from FrostStream storage configuration.
/// Provider-specific factories are preferred over the legacy connection-string registry.
/// </summary>
public static class FluentStoreFactory
{
    public static IStore CreateStorage(StorageConfigResponse config)
    {
        if (!config.Found || config.Method is null || string.IsNullOrWhiteSpace(config.Parameters))
        {
            throw new InvalidOperationException(
                $"Storage config not found for key: {config.Key ?? "<unknown>"}");
        }

        if (!StorageParametersSerializer.TryDeserialize(config.Method.Value, config.Parameters, out var parameters, out var error) ||
            parameters is null)
        {
            throw new InvalidOperationException(
                $"Invalid storage parameters for method '{config.Method}': {error ?? "unable to parse parameters"}");
        }

        return parameters switch
        {
            PosixLocalStorageParameters local => CreateDiskStore(LocalStoragePathResolver.Resolve(local.Path)),
            StreamingNetworkStorageParameters network => CreateNetworkStore(network),
            S3CompatibleObjectStorageParameters s3 => CreateS3Store(s3),
            AzureBlobObjectStorageParameters azure => CreateAzureStore(azure),
            GoogleCloudStorageObjectStorageParameters gcs => CreateGcsStore(gcs),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameters), parameters.GetType().Name, "Unsupported storage parameters")
        };
    }

    private static IStore CreateNetworkStore(StreamingNetworkStorageParameters parameters)
    {
        return parameters.Protocol switch
        {
            NetworkStorageProtocol.Ftp => WithBasePath(CreateFtpStore(parameters, useTls: false), parameters.BasePath),
            NetworkStorageProtocol.Ftps => WithBasePath(CreateFtpStore(parameters, useTls: true), parameters.BasePath),
            NetworkStorageProtocol.Sftp => WithBasePath(CreateSftpStore(parameters), parameters.BasePath),
            NetworkStorageProtocol.Nfs or NetworkStorageProtocol.Smb or NetworkStorageProtocol.Cifs =>
                CreateMountedNetworkStore(parameters),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameters.Protocol), parameters.Protocol, "Unsupported network storage protocol")
        };
    }

    private static IStore CreateFtpStore(StreamingNetworkStorageParameters parameters, bool useTls)
    {
        var credentials = new NetworkCredential(parameters.Username, parameters.Password);
        var client = new AsyncFtpClient(parameters.Host, credentials, parameters.Port ?? 0);
        if (useTls)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = false;
        }

        return new FtpStore(client, dispose: true);
    }

    private static IStore CreateSftpStore(StreamingNetworkStorageParameters parameters)
    {
        var username = parameters.Username
            ?? throw new InvalidOperationException("SFTP storage requires a username.");
        var port = parameters.Port ?? 22;

        if (!string.IsNullOrWhiteSpace(parameters.PrivateKey))
        {
            var keyBytes = Encoding.UTF8.GetBytes(parameters.PrivateKey);
            var keyStream = new MemoryStream(keyBytes, writable: false);
            var keyFile = new PrivateKeyFile(keyStream);
            return SftpStorage.FromPrivateKey(parameters.Host, port, username, keyFile);
        }

        return SftpStorage.FromCredentials(
            parameters.Host,
            port,
            username,
            parameters.Password ?? throw new InvalidOperationException("SFTP storage requires a password or private key."));
    }

    private static IStore CreateMountedNetworkStore(StreamingNetworkStorageParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.MountPath))
        {
            throw new InvalidOperationException(
                $"{parameters.Protocol} storage requires an absolute mountPath. Mount the network share on every FrostStream node before using it.");
        }

        if (!Path.IsPathFullyQualified(parameters.MountPath))
        {
            throw new InvalidOperationException(
                $"{parameters.Protocol} mountPath must be absolute: {parameters.MountPath}");
        }

        var mountPath = Path.GetFullPath(parameters.MountPath);
        if (!Directory.Exists(mountPath))
        {
            throw new DirectoryNotFoundException(
                $"{parameters.Protocol} mountPath does not exist or is not mounted: {mountPath}");
        }

        return CreateDiskStore(mountPath);
    }

    private static IStore CreateDiskStore(string path)
        => StorageFactory.Disk(Path.GetFullPath(path));

    private static IStore CreateS3Store(S3CompatibleObjectStorageParameters parameters)
    {
        if (parameters.Provider == S3CompatibleObjectStorageProvider.MinIo)
        {
            return CreateNativeMinioStore(parameters);
        }

        if (string.IsNullOrWhiteSpace(parameters.Endpoint))
        {
            return AwsS3Storage.FromCredentials(
                parameters.AccessKeyId,
                parameters.SecretKeyId,
                parameters.SessionTokenSecretId,
                parameters.BucketName,
                parameters.Region ?? "us-east-1");
        }

        var config = new AmazonS3Config
        {
            ServiceURL = parameters.Endpoint,
            ForcePathStyle = parameters.ForcePathStyle,
            UseHttp = parameters.UseSsl is false
        };
        if (!string.IsNullOrWhiteSpace(parameters.Region))
        {
            config.AuthenticationRegion = parameters.Region;
        }

        return AwsS3Storage.FromThirdPartyCredentials(
            parameters.AccessKeyId,
            parameters.SecretKeyId,
            parameters.SessionTokenSecretId,
            parameters.BucketName,
            config);
    }

    private static IStore CreateNativeMinioStore(S3CompatibleObjectStorageParameters parameters)
    {
        var configuredEndpoint = parameters.Endpoint
            ?? throw new InvalidOperationException("MinIO endpoint is required.");
        var hasAbsoluteUri = Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var uri);
        var endpoint = hasAbsoluteUri ? uri!.Authority : configuredEndpoint;
        var useSsl = parameters.UseSsl ?? (hasAbsoluteUri && uri!.Scheme == Uri.UriSchemeHttps);

        return string.IsNullOrWhiteSpace(parameters.SessionTokenSecretId)
            ? MinioStorage.FromCredentials(
                endpoint,
                parameters.AccessKeyId,
                parameters.SecretKeyId,
                parameters.BucketName,
                useSsl,
                parameters.Region)
            : MinioStorage.FromSts(
                endpoint,
                parameters.AccessKeyId,
                parameters.SecretKeyId,
                parameters.SessionTokenSecretId,
                parameters.BucketName,
                useSsl,
                parameters.Region);
    }

    private static IStore CreateAzureStore(AzureBlobObjectStorageParameters parameters)
    {
        return parameters.CredentialMode switch
        {
            AzureBlobCredentialMode.AccountKey => AzureBlobStorage.FromClient(
                CreateAzureSharedKeyClient(parameters), parameters.ContainerName),
            AzureBlobCredentialMode.ConnectionString => AzureBlobStorage.FromClient(
                new BlobServiceClient(parameters.AzureConnectionStringSecretId
                    ?? throw new InvalidOperationException("Azure connection string is required.")),
                parameters.ContainerName),
            AzureBlobCredentialMode.SasUrl => AzureBlobStorage.FromSas(
                parameters.AzureSasUrlSecretId
                    ?? throw new InvalidOperationException("Azure SAS URL is required.")),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameters.CredentialMode), parameters.CredentialMode, "Unsupported Azure Blob credential mode")
        };
    }

    private static BlobServiceClient CreateAzureSharedKeyClient(AzureBlobObjectStorageParameters parameters)
    {
        var account = parameters.AzureAccountName
            ?? throw new InvalidOperationException("Azure account name is required.");
        var key = parameters.AzureAccountKeySecretId
            ?? throw new InvalidOperationException("Azure account key is required.");
        return new BlobServiceClient(
            new Uri($"https://{account}.blob.core.windows.net"),
            new Azure.Storage.StorageSharedKeyCredential(account, key));
    }

    private static IStore CreateGcsStore(GoogleCloudStorageObjectStorageParameters parameters)
    {
        return parameters.CredentialMode switch
        {
            GoogleCloudStorageCredentialMode.CredentialsJson => GoogleCloudStorage.FromJson(
                parameters.BucketName,
                GetGoogleCredentialsValue(parameters),
                parameters.GcpCredentialsJsonIsBase64Encoded),
            GoogleCloudStorageCredentialMode.CredentialsFilePath => GoogleCloudStorage.FromJsonFile(
                parameters.BucketName,
                parameters.GcpCredentialsFilePath
                    ?? throw new InvalidOperationException("GCP credentials file path is required.")),
            GoogleCloudStorageCredentialMode.WorkloadIdentity or GoogleCloudStorageCredentialMode.DefaultCredentials =>
                GoogleCloudStorage.FromEnvironmentVariable(parameters.BucketName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameters.CredentialMode), parameters.CredentialMode, "Unsupported GCP credential mode")
        };
    }

    private static string GetGoogleCredentialsValue(GoogleCloudStorageObjectStorageParameters parameters)
    {
        if (parameters.GcpCredentialsJson is null)
        {
            throw new InvalidOperationException("GCP credentials JSON is required.");
        }

        if (parameters.GcpCredentialsJson.Value.ValueKind == JsonValueKind.String)
        {
            return parameters.GcpCredentialsJson.Value.GetString()
                ?? throw new InvalidOperationException("GCP credentials JSON is empty.");
        }

        return parameters.GcpCredentialsJson.Value.GetRawText();
    }

    private static IStore WithBasePath(IStore store, string? basePath)
        => string.IsNullOrWhiteSpace(basePath) ? store : new RootedStore(store, basePath);
}
