namespace Shared.Secrets;

public interface ISecretStore
{
    Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}
