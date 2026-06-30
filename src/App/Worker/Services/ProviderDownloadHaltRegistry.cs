using System.Collections.Concurrent;
using NodaTime;

namespace Worker.Services;

public sealed class ProviderDownloadHaltRegistry(IClock clock)
{
    private readonly ConcurrentDictionary<string, ProviderDownloadHalt> _halts = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetHalt(string? provider, out ProviderDownloadHalt halt)
    {
        halt = default!;
        return !string.IsNullOrWhiteSpace(provider)
               && _halts.TryGetValue(provider.Trim(), out halt!);
    }

    internal ProviderDownloadHalt Record(ProviderAccessFailure failure)
    {
        var halt = new ProviderDownloadHalt(
            failure.Provider,
            failure.ErrorCode,
            failure.Description,
            clock.GetCurrentInstant());

        return _halts.AddOrUpdate(failure.Provider, halt, (_, existing) => existing);
    }
}

public sealed record ProviderDownloadHalt(
    string Provider,
    string ErrorCode,
    string ErrorMessage,
    Instant HaltedAt);
