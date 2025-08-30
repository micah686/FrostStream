using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostStream.MessageHub.New
{
    // Background sweeper that evicts services not updated within a cutoff (default 5 minutes)
    public sealed class ServiceRegistryCleanup : BackgroundService
    {
        private readonly ServiceRegistry _registry;
        private readonly ILogger<ServiceRegistryCleanup> _logger;
        private readonly TimeSpan _staleCutoff;   // e.g., 5 minutes
        private readonly TimeSpan _scanPeriod;    // how often we scan, e.g., 30 seconds

        public ServiceRegistryCleanup(
            ServiceRegistry registry,
            ILogger<ServiceRegistryCleanup> logger,
            TimeSpan? staleCutoff = null,
            TimeSpan? scanPeriod = null)
        {
            _registry = registry;
            _logger = logger;
            _staleCutoff = staleCutoff ?? TimeSpan.FromMinutes(5);
            _scanPeriod = scanPeriod ?? TimeSpan.FromSeconds(30);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_scanPeriod);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);

                    var now = DateTimeOffset.UtcNow;
                    // Take a snapshot of keys to remove
                    var toRemove = _registry
                        .GetAll() // must be thread-safe snapshot or at least enumerable
                        .Where(svc => (now - svc.LastSeenUtc) > _staleCutoff)
                        .Select(svc => svc.ServiceName)
                        .ToArray();
                    foreach (var name in toRemove)
                        _registry.Remove(name);
                    if (toRemove.Length > 0)
                        _logger.LogInformation("ServiceRegistryCleanup evicted {Count} stale entries (cutoff {Cutoff}).",
                            toRemove.Length, _staleCutoff);
                    //see about adding a static state to broker that if not haivng 1 worker,databridge,webapi, it sends a shutdown command to all other services,
                    //and broker shuts down

                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // normal shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during ServiceRegistry cleanup.");
                    // Optional: small backoff to avoid tight exception loop
                    try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { /* ignore */ }
                }
            }
        }        
    }
}
