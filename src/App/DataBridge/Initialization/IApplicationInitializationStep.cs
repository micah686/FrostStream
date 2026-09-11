namespace DataBridge.Initialization;

public interface IApplicationInitializationStep
{
    string Name { get; }
    IReadOnlyCollection<string> Dependencies { get; }
    TimeSpan Timeout { get; }
    Task ExecuteAsync(CancellationToken cancellationToken);
}
