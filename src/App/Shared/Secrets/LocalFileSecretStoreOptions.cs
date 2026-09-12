namespace Shared.Secrets;

public sealed class LocalFileSecretStoreOptions
{
    public const string SectionName = "LiteSecrets";
    public const string EncryptionPurpose = "FrostStream.LocalFileSecretStore.v2";
    public const string AlgorithmName = "XChaCha20-Poly1305";
    public const string KeyFileName = "master-key.nsec";

    public string SecretsPath { get; set; } = string.Empty;
    public string KeyDirectoryPath { get; set; } = string.Empty;
    public int MaximumDocumentBytes { get; set; } = 1024 * 1024;
}

public sealed class LocalSecretRecoveryException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
