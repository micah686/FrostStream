using System.Buffers;
using System.IO.Hashing;
using FluentStorage.Storage;

namespace DataBridge.Lite;

internal static class LiteArtifactReconciler
{
    public static async Task<bool> MatchesAsync(
        IStore storage,
        string storagePath,
        string expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (!await storage.ObjectExists(storagePath, cancellationToken)) return false;
        if (await storage.GetObjectLength(storagePath, -1, cancellationToken) != expectedLength) return false;

        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var stream = await storage.OpenRead(storagePath, cancellationToken);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                hasher.Append(buffer.AsSpan(0, read));
            return string.Equals(
                Convert.ToHexStringLower(hasher.GetCurrentHash()), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
