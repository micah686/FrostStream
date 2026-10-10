namespace DataBridge.Persistence;

public interface IApplicationSchemaInitializer
{
    void Initialize(CancellationToken cancellationToken = default);
}
