using System.Net.Http;
using System.Text;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Messaging;

namespace Shared.Pot;

/// <summary>
/// Reusable POT broker. Subscribes to <see cref="PotSubjects.Request"/> in the
/// <see cref="PotSubjects.BrokersQueueGroup"/> queue group and fulfils each request by replaying the
/// tunneled HTTP call against a nearby bgutil provider. Because POT tokens are bound to the
/// visitor/account context carried in the request body (not the broker's IP), any healthy broker can
/// answer any request.
///
/// The broker is health-gated: it only joins the queue group while the provider's <c>/ping</c>
/// succeeds, and leaves it when the provider goes unhealthy, so NATS routes around dead providers.
/// </summary>
public sealed class PotBrokerConsumerService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly PotBrokerOptions _options;
    private readonly ILogger<PotBrokerConsumerService> _logger;
    private readonly HttpClient _httpClient;

    private Uri? _providerBase;
    private ISubscription? _subscription;
    private CancellationToken _stoppingToken;

    public PotBrokerConsumerService(
        IMessageBus messageBus,
        IOptions<PotBrokerOptions> options,
        ILogger<PotBrokerConsumerService> logger)
    {
        _messageBus = messageBus;
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = _options.RequestTimeout };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ProviderUrl))
        {
            _logger.LogWarning("PotBroker is enabled but no PotBroker:ProviderUrl is configured; not serving POT requests.");
            return;
        }

        _stoppingToken = stoppingToken;
        // Normalise to a trailing slash so relative paths (get_pot, ping) resolve correctly.
        var baseUrl = _options.ProviderUrl.EndsWith('/') ? _options.ProviderUrl : _options.ProviderUrl + "/";
        _providerBase = new Uri(baseUrl, UriKind.Absolute);

        _logger.LogInformation("PotBroker starting; provider {ProviderUrl}.", _providerBase);

        using var timer = new PeriodicTimer(_options.HealthCheckInterval);
        try
        {
            do
            {
                await ReconcileSubscriptionAsync().ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            await DropSubscriptionAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ReconcileSubscriptionAsync()
    {
        var healthy = await IsProviderHealthyAsync().ConfigureAwait(false);

        if (healthy && _subscription is null)
        {
            _subscription = await _messageBus.SubscribeAsync<PotTunnelRequest>(
                PotSubjects.Request,
                HandleRequestAsync,
                queueGroup: PotSubjects.BrokersQueueGroup,
                cancellationToken: _stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("PotBroker joined the '{QueueGroup}' queue group (provider healthy).", PotSubjects.BrokersQueueGroup);
        }
        else if (!healthy && _subscription is not null)
        {
            await DropSubscriptionAsync(_stoppingToken).ConfigureAwait(false);
            _logger.LogWarning("PotBroker left the queue group; provider {ProviderUrl} is unhealthy.", _providerBase);
        }
    }

    private async Task DropSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_subscription is null)
        {
            return;
        }

        var subscription = _subscription;
        _subscription = null;
        await subscription.StopAsync(cancellationToken).ConfigureAwait(false);
        await subscription.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> IsProviderHealthyAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _httpClient.GetAsync(new Uri(_providerBase!, "ping"), cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PotBroker health check against {ProviderUrl} failed.", _providerBase);
            return false;
        }
    }

    private async Task HandleRequestAsync(IMessageContext<PotTunnelRequest> context)
    {
        var request = context.Message;
        try
        {
            using var httpRequest = new HttpRequestMessage(
                new HttpMethod(request.Method),
                new Uri(_providerBase!, request.Path.TrimStart('/')));

            if (request.Body is not null)
            {
                httpRequest.Content = new StringContent(
                    request.Body,
                    Encoding.UTF8,
                    request.ContentType ?? "application/json");
            }

            using var httpResponse = await _httpClient.SendAsync(httpRequest, _stoppingToken).ConfigureAwait(false);
            var body = await httpResponse.Content.ReadAsStringAsync(_stoppingToken).ConfigureAwait(false);

            await context.RespondAsync(new PotTunnelResponse
            {
                StatusCode = (int)httpResponse.StatusCode,
                Body = body,
                ContentType = httpResponse.Content.Headers.ContentType?.ToString()
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "PotBroker failed to fulfil {Method} {Path}.", request.Method, request.Path);
            await context.RespondAsync(new PotTunnelResponse
            {
                StatusCode = 502,
                Body = $"POT broker error: {ex.Message}",
                ContentType = "text/plain"
            }).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
