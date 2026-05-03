using System.Globalization;
using System.IO.Hashing;
using System.Text;
using NodaTime;

namespace Shared.Media;

public static class MediaSourceIdentity
{
    private const string BasisVersion = "froststream-source-metadata-v1";

    public static string? TryCreateSourceMetadataHash(
        string? provider,
        string? sourceMediaId,
        Instant? sourceLastModified)
    {
        var normalizedProvider = NormalizeRequired(provider);
        var normalizedSourceMediaId = NormalizeRequired(sourceMediaId);
        if (normalizedProvider is null || normalizedSourceMediaId is null)
            return null;

        var normalizedLastModified = sourceLastModified is { } instant
            ? instant.ToDateTimeOffset().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "null";

        var basis = CreateLengthPrefixedBasis(
            BasisVersion,
            normalizedProvider.ToLowerInvariant(),
            normalizedSourceMediaId,
            normalizedLastModified);

        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(Encoding.UTF8.GetBytes(basis), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeRequired(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string CreateLengthPrefixedBasis(params string[] components)
    {
        var builder = new StringBuilder();
        foreach (var component in components)
        {
            builder
                .Append(component.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(component)
                .Append('\n');
        }

        return builder.ToString();
    }
}
