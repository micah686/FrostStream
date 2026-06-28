using System.Net;
using System.Net.Sockets;
using System.Text;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Messaging;

namespace Worker.Services;

/// <summary>
/// Loopback HTTP→NATS bridge. The bgutil yt-dlp plugin only speaks HTTP to a <c>base_url</c>, so the
/// Worker hosts this tiny listener on 127.0.0.1 and points the plugin at it. Each call the plugin
/// makes (e.g. <c>POST /get_pot</c>, <c>GET /ping</c>) is tunneled over NATS to the <c>pot-brokers</c>
/// queue group, which replays it against a nearby bgutil provider and returns the response. Tokens are
/// bound to the request's visitor/account context (not the broker's IP), so any healthy broker works.
/// </summary>
public sealed class PotShimService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly PotProviderOptions _options;
    private readonly PotShimEndpoint _endpoint;
    private readonly ILogger<PotShimService> _logger;
    private readonly string? _prefix;

    public PotShimService(
        IMessageBus messageBus,
        IOptions<PotProviderOptions> options,
        PotShimEndpoint endpoint,
        ILogger<PotShimService> logger)
    {
        _messageBus = messageBus;
        _options = options.Value;
        _endpoint = endpoint;
        _logger = logger;

        if (_options.Enabled)
        {
            // Resolve the port eagerly (in the constructor) so the base URL is published before any
            // BackgroundService starts processing downloads.
            var port = FindFreeLoopbackPort();
            _prefix = $"http://127.0.0.1:{port}/";
            _endpoint.BaseUrl = $"http://127.0.0.1:{port}";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _prefix is null)
        {
            return;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(_prefix);
        listener.Start();
        _logger.LogInformation("POT shim listening on {BaseUrl} (tunnelling to '{QueueGroup}' over NATS).",
            _endpoint.BaseUrl, PotSubjects.BrokersQueueGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Handle each request independently so a slow token solve doesn't block other calls.
                _ = HandleContextAsync(context, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;

            string? body = null;
            if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var tunnelRequest = new PotTunnelRequest
            {
                Method = request.HttpMethod,
                Path = request.Url?.PathAndQuery ?? "/",
                Body = body,
                ContentType = request.ContentType
            };

            PotTunnelResponse? response;
            try
            {
                response = await _messageBus.RequestAsync<PotTunnelRequest, PotTunnelResponse>(
                    PotSubjects.Request,
                    tunnelRequest,
                    _options.RequestTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "POT shim failed tunnelling {Method} {Path}.", tunnelRequest.Method, tunnelRequest.Path);
                response = null;
            }

            await WriteResponseAsync(context.Response, response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "POT shim could not complete a request.");
            TryAbort(context);
        }
    }

    private static async Task WriteResponseAsync(
        HttpListenerResponse response,
        PotTunnelResponse? tunnelResponse,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        if (tunnelResponse is null)
        {
            // No broker answered in time — surface a gateway timeout so yt-dlp reports it cleanly.
            response.StatusCode = (int)HttpStatusCode.GatewayTimeout;
            response.ContentType = "text/plain";
            payload = Encoding.UTF8.GetBytes("No POT broker available.");
        }
        else
        {
            response.StatusCode = tunnelResponse.StatusCode;
            if (!string.IsNullOrEmpty(tunnelResponse.ContentType))
            {
                response.ContentType = tunnelResponse.ContentType;
            }

            payload = Encoding.UTF8.GetBytes(tunnelResponse.Body ?? string.Empty);
        }

        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static void TryAbort(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    private static int FindFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
