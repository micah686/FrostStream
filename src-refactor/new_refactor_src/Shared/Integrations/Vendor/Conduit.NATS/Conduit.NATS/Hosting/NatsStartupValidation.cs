using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Conduit.NATS;

/// <summary>
/// Startup-only reachability check: pings the NATS server once so misconfiguration fails fast.
/// Steady-state liveness is the v3 client's job (built-in reconnect), so there is no ongoing probe.
/// Registered only when topology provisioning is not already waiting for the connection.
/// </summary>
internal sealed class NatsStartupValidation : IHostedService
{
    private readonly INatsConnection _connection;
    private readonly ILogger<NatsStartupValidation> _logger;
    private readonly ConduitNatsOptions _options;

    public NatsStartupValidation(INatsConnection connection, ILogger<NatsStartupValidation> logger, IOptions<ConduitNatsOptions> options)
    {
        _connection = connection;
        _logger = logger;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.ValidateConnectionOnStart)
            return;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.StartupConnectionTimeout);
        try
        {
            await _connection.PingAsync(timeoutCts.Token);
            _logger.LogInformation("NATS connection validated ({Url}).", _options.Url);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"NATS server at '{_options.Url}' did not respond to ping within {_options.StartupConnectionTimeout}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
