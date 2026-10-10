namespace Conduit.NATS;

/// <summary>
/// Thin passthrough over a NATS Object Store bucket. FrostStream moves media bytes through
/// FluentStorage; this seam handles only small manifests and staged-import files by key.
/// Resolve a per-bucket instance via the injected <c>Func&lt;string, IObjectStore&gt;</c> factory.
/// </summary>
public interface IObjectStore : IAsyncDisposable
{
    /// <summary>Stores <paramref name="data"/> under <paramref name="key"/>; returns the stored object name.</summary>
    Task<string> PutAsync(string key, Stream data, CancellationToken cancellationToken = default);

    /// <summary>Downloads the object for <paramref name="key"/> directly into <paramref name="target"/>.</summary>
    Task GetAsync(string key, Stream target, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object for <paramref name="key"/>. Idempotent — a missing key is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
