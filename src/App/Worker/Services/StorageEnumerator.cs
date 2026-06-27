using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentStorage.Blobs;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Shared.Storage;

namespace Worker.Services;

public sealed class StorageEnumerator(
    IStorageConfigClient storageConfigClient,
    IBlobStorageProvider fallbackBlobStorageProvider,
    ILogger<StorageEnumerator> logger) : IStorageEnumerator
{
    public async IAsyncEnumerable<string> EnumerateFilePathsAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = await storageConfigClient.GetStorageConfigAsync(storageKey, cancellationToken);
        if (!config.Found || config.Method is null || string.IsNullOrWhiteSpace(config.Parameters))
        {
            throw new InvalidOperationException($"Storage config not found for key: {storageKey}");
        }

        if (!StorageParametersSerializer.TryDeserialize(config.Method.Value, config.Parameters, out var parameters, out var error) ||
            parameters is null)
        {
            throw new InvalidOperationException(
                $"Invalid storage parameters for key '{storageKey}' method '{config.Method}': {error ?? "unable to parse parameters"}");
        }

        switch (parameters)
        {
            case PosixLocalStorageParameters local:
                await foreach (var path in EnumerateLocalAsync(local, cancellationToken))
                {
                    yield return path;
                }
                yield break;

            case S3CompatibleObjectStorageParameters s3:
                await foreach (var path in EnumerateS3Async(s3, cancellationToken))
                {
                    yield return path;
                }
                yield break;

            case AzureBlobObjectStorageParameters azure:
                await foreach (var path in EnumerateAzureAsync(azure, cancellationToken))
                {
                    yield return path;
                }
                yield break;

            case GoogleCloudStorageObjectStorageParameters gcs:
                await foreach (var path in EnumerateGcsAsync(gcs, cancellationToken))
                {
                    yield return path;
                }
                yield break;

            default:
                logger.LogWarning(
                    "Filesystem rescan storage listing for {StorageKey} method {Method} uses FluentStorage ListAsync fallback, which materializes the backend listing before upload.",
                    storageKey,
                    config.Method);

                await foreach (var path in EnumerateFluentStorageFallbackAsync(storageKey, cancellationToken))
                {
                    yield return path;
                }
                yield break;
        }
    }

    private static async IAsyncEnumerable<string> EnumerateLocalAsync(
        PosixLocalStorageParameters parameters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(LocalStoragePathResolver.Resolve(parameters.Path));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Local storage path does not exist: {root}");
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> EnumerateS3Async(
        S3CompatibleObjectStorageParameters parameters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = CreateS3Client(parameters);
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = parameters.BucketName,
                ContinuationToken = continuationToken,
                MaxKeys = 1000
            }, cancellationToken);

            foreach (var item in response.S3Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(item.Key) && !item.Key.EndsWith("/", StringComparison.Ordinal))
                {
                    yield return item.Key;
                }
            }

            continuationToken = response.IsTruncated.GetValueOrDefault()
                ? response.NextContinuationToken
                : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    private static AmazonS3Client CreateS3Client(S3CompatibleObjectStorageParameters parameters)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = parameters.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(parameters.Endpoint))
        {
            config.ServiceURL = parameters.Endpoint;
            if (!string.IsNullOrWhiteSpace(parameters.Region))
            {
                config.AuthenticationRegion = parameters.Region;
            }
        }
        else if (!string.IsNullOrWhiteSpace(parameters.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(parameters.Region);
        }

        if (parameters.UseSsl is false)
        {
            config.UseHttp = true;
        }

        AWSCredentials credentials = string.IsNullOrWhiteSpace(parameters.SessionTokenSecretId)
            ? new BasicAWSCredentials(parameters.AccessKeyId, parameters.SecretKeyId)
            : new SessionAWSCredentials(parameters.AccessKeyId, parameters.SecretKeyId, parameters.SessionTokenSecretId);

        return new AmazonS3Client(credentials, config);
    }

    private static async IAsyncEnumerable<string> EnumerateAzureAsync(
        AzureBlobObjectStorageParameters parameters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var container = CreateAzureContainerClient(parameters);
        await foreach (var blob in container.GetBlobsAsync(
                           traits: BlobTraits.None,
                           states: BlobStates.None,
                           prefix: null,
                           cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(blob.Name))
            {
                yield return blob.Name;
            }
        }
    }

    private static BlobContainerClient CreateAzureContainerClient(AzureBlobObjectStorageParameters parameters)
    {
        return parameters.CredentialMode switch
        {
            AzureBlobCredentialMode.AccountKey => CreateAzureAccountKeyContainerClient(parameters),
            AzureBlobCredentialMode.ConnectionString => CreateAzureConnectionStringContainerClient(parameters),
            AzureBlobCredentialMode.SasUrl => CreateAzureSasContainerClient(parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(parameters.CredentialMode), parameters.CredentialMode, "Unsupported Azure Blob credential mode")
        };
    }

    private static BlobContainerClient CreateAzureAccountKeyContainerClient(AzureBlobObjectStorageParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.AzureAccountName) ||
            string.IsNullOrWhiteSpace(parameters.AzureAccountKeySecretId) ||
            string.IsNullOrWhiteSpace(parameters.ContainerName))
        {
            throw new InvalidOperationException("Azure account-key storage requires account name, account key, and container name.");
        }

        var credential = new StorageSharedKeyCredential(parameters.AzureAccountName, parameters.AzureAccountKeySecretId);
        var serviceUri = new Uri($"https://{parameters.AzureAccountName}.blob.core.windows.net");
        return new BlobServiceClient(serviceUri, credential).GetBlobContainerClient(parameters.ContainerName);
    }

    private static BlobContainerClient CreateAzureConnectionStringContainerClient(AzureBlobObjectStorageParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.AzureConnectionStringSecretId) ||
            string.IsNullOrWhiteSpace(parameters.ContainerName))
        {
            throw new InvalidOperationException("Azure connection-string storage requires a connection string and container name.");
        }

        return new BlobServiceClient(parameters.AzureConnectionStringSecretId)
            .GetBlobContainerClient(parameters.ContainerName);
    }

    private static BlobContainerClient CreateAzureSasContainerClient(AzureBlobObjectStorageParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.AzureSasUrlSecretId))
        {
            throw new InvalidOperationException("Azure SAS storage requires a SAS URL.");
        }

        var uri = new Uri(parameters.AzureSasUrlSecretId);
        if (!string.IsNullOrWhiteSpace(parameters.ContainerName) &&
            !uri.AbsolutePath.Trim('/').Equals(parameters.ContainerName, StringComparison.OrdinalIgnoreCase))
        {
            return new BlobServiceClient(uri)
                .GetBlobContainerClient(parameters.ContainerName);
        }

        return new BlobContainerClient(uri);
    }

    private static async IAsyncEnumerable<string> EnumerateGcsAsync(
        GoogleCloudStorageObjectStorageParameters parameters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await CreateGcsClientAsync(parameters, cancellationToken);
        var options = new ListObjectsOptions { Projection = Projection.NoAcl };
        await foreach (var obj in client.ListObjectsAsync(parameters.BucketName, prefix: null, options)
                           .WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(obj.Name) && !obj.Name.EndsWith("/", StringComparison.Ordinal))
            {
                yield return obj.Name;
            }
        }
    }

    // Credentials originate from admin-configured OpenBao secret store, not untrusted user input,
    // so the security concern behind the GoogleCredential deprecations does not apply here.
#pragma warning disable CS0618
    private static async Task<StorageClient> CreateGcsClientAsync(
        GoogleCloudStorageObjectStorageParameters parameters,
        CancellationToken cancellationToken)
    {
        GoogleCredential? credential = parameters.CredentialMode switch
        {
            GoogleCloudStorageCredentialMode.CredentialsJson when parameters.GcpCredentialsJson is not null =>
                await GoogleCredential.FromStreamAsync(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(GetGoogleCredentialsJson(parameters))),
                    cancellationToken),
            GoogleCloudStorageCredentialMode.CredentialsFilePath when !string.IsNullOrWhiteSpace(parameters.GcpCredentialsFilePath) =>
                await GoogleCredential.FromFileAsync(parameters.GcpCredentialsFilePath, cancellationToken),
            GoogleCloudStorageCredentialMode.WorkloadIdentity or GoogleCloudStorageCredentialMode.DefaultCredentials =>
                GoogleCredential.GetApplicationDefault(),
            _ => null
        };

        return StorageClient.Create(credential);
    }
#pragma warning restore CS0618

    private static string GetGoogleCredentialsJson(GoogleCloudStorageObjectStorageParameters parameters)
    {
        if (parameters.GcpCredentialsJson is null)
        {
            throw new InvalidOperationException("GCP credentials JSON is required.");
        }

        if (!parameters.GcpCredentialsJsonIsBase64Encoded)
        {
            return parameters.GcpCredentialsJson.Value.GetRawText();
        }

        var encoded = parameters.GcpCredentialsJson.Value.ValueKind == System.Text.Json.JsonValueKind.String
            ? parameters.GcpCredentialsJson.Value.GetString()
            : parameters.GcpCredentialsJson.Value.GetRawText();

        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException("Base64-encoded GCP credentials JSON is empty.");
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private async IAsyncEnumerable<string> EnumerateFluentStorageFallbackAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var blobStorage = await fallbackBlobStorageProvider.GetAsync(storageKey, cancellationToken);
        var blobs = await blobStorage.ListAsync(new ListOptions { Recurse = true }, cancellationToken);
        foreach (var blob in blobs.Where(b => b.IsFile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return blob.FullPath;
            await Task.Yield();
        }
    }
}
