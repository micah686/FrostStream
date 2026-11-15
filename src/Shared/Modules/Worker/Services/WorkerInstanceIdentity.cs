using Microsoft.Extensions.Options;

namespace Worker.Services;

/// <summary>One stable identity for registry heartbeats and download leases.</summary>
public sealed class WorkerInstanceIdentity(IOptions<WorkerOptions> options)
{
    public string Id { get; } = Resolve(options.Value);

    private static string Resolve(WorkerOptions options)
    {
        var configured = Environment.GetEnvironmentVariable("FROSTSTREAM_WORKER_ID");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return string.IsNullOrWhiteSpace(options.Name) ? Environment.MachineName : options.Name.Trim();
    }
}
