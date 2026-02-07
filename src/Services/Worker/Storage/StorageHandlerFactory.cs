using Shared;

namespace Worker.Storage;

/// <summary>
/// Factory for resolving storage handlers based on the configured storage method.
/// </summary>
public class StorageHandlerFactory
{
    private readonly Dictionary<StorageMethod, IStorageHandler> _handlers;

    public StorageHandlerFactory(IEnumerable<IStorageHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.SupportedMethod);
    }

    /// <summary>
    /// Gets the appropriate handler for the specified storage method.
    /// </summary>
    /// <param name="method">The storage method to get a handler for.</param>
    /// <returns>The storage handler for the specified method.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no handler is registered for the specified method.</exception>
    public IStorageHandler GetHandler(StorageMethod method)
    {
        if (_handlers.TryGetValue(method, out var handler))
        {
            return handler;
        }

        throw new InvalidOperationException(
            $"No storage handler registered for method {method}. " +
            $"Available handlers: {string.Join(", ", _handlers.Keys)}");
    }
}
