using System.Globalization;

namespace DataBridge.Flows;

internal static class LocalImportFlowInstance
{
    private const string OperationPrefix = "local-import-item/";

    public static string ForItemAttempt(Guid itemId, int attempt)
        => $"{itemId:N}/attempt-{Math.Max(1, attempt).ToString(CultureInfo.InvariantCulture)}";

    public static string OperationKey(Guid itemId, int attempt, string suffix)
        => $"{OperationPrefix}{itemId:N}/attempt/{Math.Max(1, attempt).ToString(CultureInfo.InvariantCulture)}/{suffix}";

    public static string FromOperationKey(Guid itemId, string operationKey)
    {
        if (!operationKey.StartsWith(OperationPrefix, StringComparison.Ordinal))
            return itemId.ToString("N");

        var remainder = operationKey[OperationPrefix.Length..];
        var parts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && string.Equals(parts[0], itemId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "attempt", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt))
            return ForItemAttempt(itemId, attempt);

        return itemId.ToString("N");
    }
}
