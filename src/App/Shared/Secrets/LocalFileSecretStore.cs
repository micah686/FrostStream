using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NSec.Cryptography;

namespace Shared.Secrets;

/// <summary>
/// XChaCha20-Poly1305 encrypted, atomic file-backed secret documents for a single local
/// FrostStream installation. The NSec master key is stored separately so backup and restore
/// code must preserve both the key and ciphertext explicitly.
/// </summary>
public sealed partial class LocalFileSecretStore : ISecretStore, IDisposable
{
    private const int MaximumSegments = 8;
    private const int MaximumPathLength = 512;
    private const int MaximumFields = 128;
    private const int MaximumKeyBlobBytes = 4096;
    private static readonly byte[] EnvelopeHeader = "FSLNSC02"u8.ToArray();
    private static readonly AeadAlgorithm Algorithm = AeadAlgorithm.XChaCha20Poly1305;

    private readonly string rootPath;
    private readonly string keyDirectoryPath;
    private readonly string keyFilePath;
    private readonly int maximumDocumentBytes;
    private readonly Key key;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> pathLocks = new(StringComparer.Ordinal);

    public LocalFileSecretStore(IOptions<LocalFileSecretStoreOptions> options)
    {
        var configured = options.Value;
        rootPath = RequireAbsolutePath(configured.SecretsPath, nameof(configured.SecretsPath));
        keyDirectoryPath = RequireAbsolutePath(configured.KeyDirectoryPath, nameof(configured.KeyDirectoryPath));
        maximumDocumentBytes = configured.MaximumDocumentBytes is >= 1024 and <= 16 * 1024 * 1024
            ? configured.MaximumDocumentBytes
            : throw new InvalidOperationException("LiteSecrets:MaximumDocumentBytes must be between 1024 and 16777216.");

        EnsureDirectory(rootPath, rootPath);
        EnsureDirectory(keyDirectoryPath, keyDirectoryPath);
        keyFilePath = Path.Combine(keyDirectoryPath, LocalFileSecretStoreOptions.KeyFileName);
        key = LoadOrCreateKey();
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeLogicalPath(path);
        var filePath = ResolvePath(normalizedPath);
        var gate = pathLocks.GetOrAdd(filePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(filePath, normalizedPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumFields)
            throw new ArgumentException($"A secret document may contain at most {MaximumFields} fields.", nameof(values));
        if (values.Any(pair => !FieldNameRegex().IsMatch(pair.Key)))
            throw new ArgumentException("Secret field names must match ^[A-Za-z0-9_.-]{1,100}$.", nameof(values));

        var normalizedPath = NormalizeLogicalPath(path);
        var orderedValues = values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(orderedValues);
        if (plaintext.Length > maximumDocumentBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new ArgumentException("Secret document exceeds the configured size limit.", nameof(values));
        }

        var filePath = ResolvePath(normalizedPath);
        var gate = pathLocks.GetOrAdd(filePath, static _ => new SemaphoreSlim(1, 1));
        var lockTaken = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            if (File.Exists(filePath))
                _ = await ReadUnlockedAsync(filePath, normalizedPath, cancellationToken).ConfigureAwait(false);

            var directory = Path.GetDirectoryName(filePath)!;
            EnsureDirectory(directory, rootPath);
            var nonce = RandomNumberGenerator.GetBytes(Algorithm.NonceSize);
            var ciphertext = Algorithm.Encrypt(key, nonce, AssociatedData(normalizedPath), plaintext);
            var envelope = new byte[EnvelopeHeader.Length + nonce.Length + ciphertext.Length];
            EnvelopeHeader.CopyTo(envelope, 0);
            nonce.CopyTo(envelope, EnvelopeHeader.Length);
            ciphertext.CopyTo(envelope, EnvelopeHeader.Length + nonce.Length);

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await WriteNewFileAsync(temporaryPath, envelope, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, filePath, overwrite: true);
                RestrictFile(filePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (lockTaken)
                gate.Release();
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeLogicalPath(path);
        var filePath = ResolvePath(normalizedPath);
        var gate = pathLocks.GetOrAdd(filePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                _ = await ReadUnlockedAsync(filePath, normalizedPath, cancellationToken).ConfigureAwait(false);
                File.Delete(filePath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ValidateExistingSecretsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*.secret", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await ReadUnlockedAsync(filePath, LogicalPathForFile(filePath), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        key.Dispose();
        foreach (var gate in pathLocks.Values)
            gate.Dispose();
        pathLocks.Clear();
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadUnlockedAsync(
        string filePath,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return null;
        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Local secret files may not be symbolic links or reparse points.");

        byte[]? plaintext = null;
        try
        {
            var envelope = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var minimumSize = EnvelopeHeader.Length + Algorithm.NonceSize + Algorithm.TagSize;
            if (envelope.Length < minimumSize || envelope.Length > maximumDocumentBytes + minimumSize)
                throw new CryptographicException("Ciphertext has an invalid size.");
            if (!CryptographicOperations.FixedTimeEquals(
                    envelope.AsSpan(0, EnvelopeHeader.Length), EnvelopeHeader))
                throw new CryptographicException("Ciphertext has an unsupported format.");

            var nonce = envelope.AsSpan(EnvelopeHeader.Length, Algorithm.NonceSize);
            var ciphertext = envelope.AsSpan(EnvelopeHeader.Length + Algorithm.NonceSize);
            plaintext = Algorithm.Decrypt(key, nonce, AssociatedData(logicalPath), ciphertext)
                        ?? throw new CryptographicException("Ciphertext authentication failed.");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                   ?? throw new JsonException("Secret document was empty.");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw RecoveryException(ex);
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private Key LoadOrCreateKey()
    {
        RejectReparsePoints(keyDirectoryPath);
        if (File.Exists(keyFilePath))
            return ImportKey();

        if (Directory.EnumerateFiles(rootPath, "*.secret", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Any())
            throw RecoveryException(new FileNotFoundException("The local secret master key is missing."));

        using var generated = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var keyBlob = generated.Export(KeyBlobFormat.NSecSymmetricKey);
        var temporaryPath = Path.Combine(keyDirectoryPath, $".{LocalFileSecretStoreOptions.KeyFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(keyBlob);
                stream.Flush(flushToDisk: true);
            }
            RestrictFile(temporaryPath);
            try
            {
                File.Move(temporaryPath, keyFilePath);
            }
            catch (IOException) when (File.Exists(keyFilePath))
            {
                // Another composition may have initialized the installation first.
            }
            RestrictFile(keyFilePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBlob);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return ImportKey();
    }

    private Key ImportKey()
    {
        byte[]? keyBlob = null;
        try
        {
            if ((File.GetAttributes(keyFilePath) & FileAttributes.ReparsePoint) != 0)
                throw new CryptographicException("The local secret master key may not be a symbolic link or reparse point.");
            keyBlob = File.ReadAllBytes(keyFilePath);
            if (keyBlob.Length is 0 or > MaximumKeyBlobBytes)
                throw new CryptographicException("The local secret master key has an invalid size.");
            RestrictFile(keyFilePath);
            return Key.Import(Algorithm, keyBlob, KeyBlobFormat.NSecSymmetricKey, new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport
            });
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException
                                   or IOException or UnauthorizedAccessException)
        {
            throw RecoveryException(ex);
        }
        finally
        {
            if (keyBlob is not null)
                CryptographicOperations.ZeroMemory(keyBlob);
        }
    }

    private static LocalSecretRecoveryException RecoveryException(Exception innerException) => new(
        "Encrypted local secrets cannot be decrypted. Restore the matching Lite NSec master key and secret files from the same backup; do not re-enter or overwrite credentials until recovery is complete.",
        innerException);

    private static byte[] AssociatedData(string logicalPath) =>
        Encoding.UTF8.GetBytes($"{LocalFileSecretStoreOptions.EncryptionPurpose}\n{logicalPath}");

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        RestrictFile(path);
    }

    private string NormalizeLogicalPath(string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath) || logicalPath.Length > MaximumPathLength)
            throw new ArgumentException("Secret path is missing or exceeds the maximum length.", nameof(logicalPath));
        if (logicalPath.StartsWith('/') || logicalPath.EndsWith('/') || logicalPath.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException("Secret paths must be relative and may not contain empty segments.", nameof(logicalPath));

        var segments = logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > MaximumSegments ||
            segments.Any(segment => !PathSegmentRegex().IsMatch(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Secret paths must contain only bounded alphanumeric, dot, underscore, or dash segments.",
                nameof(logicalPath));
        }
        return string.Join('/', segments);
    }

    private string ResolvePath(string normalizedLogicalPath)
    {
        var segments = normalizedLogicalPath.Split('/');
        var candidate = Path.GetFullPath(Path.Combine([rootPath, .. segments[..^1], $"{segments[^1]}.secret"]));
        var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("Secret path resolves outside the configured root.", nameof(normalizedLogicalPath));
        RejectReparsePoints(Path.GetDirectoryName(candidate)!);
        return candidate;
    }

    private string LogicalPathForFile(string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        if (!relative.EndsWith(".secret", StringComparison.Ordinal))
            throw new CryptographicException("Secret document has an unsupported name.");
        return relative[..^".secret".Length].Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string RequireAbsolutePath(string path, string option)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"LiteSecrets:{option} must be an absolute path.");
        return Path.GetFullPath(path);
    }

    private static void EnsureDirectory(string path, string permissionRoot)
    {
        RejectReparsePoints(path);
        Directory.CreateDirectory(path);
        RejectReparsePoints(path);
        if (!OperatingSystem.IsWindows())
        {
            var normalizedRoot = Path.GetFullPath(permissionRoot);
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                var currentPath = Path.GetFullPath(current.FullName);
                if (currentPath != normalizedRoot &&
                    !currentPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    break;
                File.SetUnixFileMode(currentPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (currentPath == normalizedRoot)
                    break;
            }
        }
    }

    private static void RejectReparsePoints(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The local secret path may not traverse symbolic links or reparse points.");
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegmentRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();
}
