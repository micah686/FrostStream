using Shared.Secrets;

namespace IntegrationTests.Infrastructure;

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _store = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(path, out var values);
        return Task.FromResult(values);
    }

    public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        _store[path] = new Dictionary<string, string>(values, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        _store.Remove(path);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        _store.Clear();
    }
}
