namespace Shared.Storage;

public sealed record StorageConfigResponse(
    bool Found,
    string? Key,
    StorageMethod? Method,
    string? Parameters,
    string? Description,
    string? WorkerTag = null)
{
    public static StorageConfigResponse NotFound(string? key)
    {
        return new StorageConfigResponse(
            Found: false,
            Key: key,
            Method: null,
            Parameters: null,
            Description: null,
            WorkerTag: null);
    }
}
