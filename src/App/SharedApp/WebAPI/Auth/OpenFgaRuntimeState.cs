namespace WebAPI.Auth;

/// <summary>
/// Holds the OpenFGA store and authorization-model ids in use at runtime. Seeded from
/// configuration and, when those values are blank, filled in by <see cref="OpenFgaProvisioner"/>
/// after it creates/locates the store and model. Read by <see cref="OpenFgaAuthorizer"/> and
/// <see cref="OpenFgaTupleWriter"/> on every request, so updates are published through volatile
/// fields.
/// </summary>
public sealed class OpenFgaRuntimeState
{
    private volatile string? _storeId;
    private volatile string? _authorizationModelId;

    public string? StoreId
    {
        get => _storeId;
        set => _storeId = value;
    }

    public string? AuthorizationModelId
    {
        get => _authorizationModelId;
        set => _authorizationModelId = value;
    }

    public bool IsReady => !string.IsNullOrWhiteSpace(_storeId);
}
