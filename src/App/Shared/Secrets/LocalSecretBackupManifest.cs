namespace Shared.Secrets;

/// <summary>The inseparable files a Lite backup must preserve to recover encrypted secrets.</summary>
public sealed record LocalSecretBackupManifest(
    string SecretsDirectory,
    string KeyDirectory,
    string KeyFileName,
    string Algorithm,
    int FormatVersion = 2);

public interface ILocalSecretBackupManifestProvider
{
    LocalSecretBackupManifest GetManifest();
}

/// <summary>
/// Phase 16 migration seam for copying only secret documents belonging to a selected owner.
/// Implementations must preserve logical SecretPaths and never export plaintext to logs.
/// </summary>
public interface ISelectedOwnerSecretMigration
{
    Task ExportAsync(string ownerSubject, Stream destination, CancellationToken cancellationToken = default);
    Task ImportAsync(string ownerSubject, Stream source, CancellationToken cancellationToken = default);
}

public sealed class LocalSecretBackupManifestProvider(
    Microsoft.Extensions.Options.IOptions<LocalFileSecretStoreOptions> options)
    : ILocalSecretBackupManifestProvider
{
    public LocalSecretBackupManifest GetManifest() => new(
        Path.GetFullPath(options.Value.SecretsPath),
        Path.GetFullPath(options.Value.KeyDirectoryPath),
        LocalFileSecretStoreOptions.KeyFileName,
        LocalFileSecretStoreOptions.AlgorithmName);
}
