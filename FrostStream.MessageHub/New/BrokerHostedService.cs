using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FrostStream.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.MessageHub.New
{
    public sealed class BrokerOptions
    {
        public string Bind { get; set; } = "tcp://*:5555";
        public int SendHighWatermark { get; set; } = 10000;// max allowed messages in memory before NetMQ starts dropping/blocking
        public int ReceiveHighWatermark { get; set; } = 10000; // max allowed messages in memory before NetMQ starts dropping/blocking
        public TimeSpan Linger { get; set; } = TimeSpan.Zero; //how long it will try to deliver messages after Close() is called
        public TimeSpan PruneInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan PresenceTtl { get; set; } = TimeSpan.FromMinutes(5);
    }
    internal class BrokerHostedService : IHostedService
    {
        private readonly ILogger<BrokerHostedService> _log;
        private readonly ServiceRegistry _registry;
        private readonly BrokerOptions _opts;
        // Optional perf cache: identityKey (Base64) -> identity bytes
        private readonly ConcurrentDictionary<string, byte[]> _idBytesCache = new();

        private RouterSocket? _router;
        private NetMQPoller? _poller;
        private NetMQTimer? _pruneTimer;
        private Task? _loopTask;
        private readonly CancellationTokenSource _cts = new();

        public BrokerHostedService(
            ILogger<BrokerHostedService> log,
            ServiceRegistry registry)
        {
            _log = log;
            _registry = registry;
            _opts = new BrokerOptions(); // wire up IOptions<BrokerOptions> if you prefer
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _loopTask = Task.Run(RunAsync, cancellationToken);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _cts.Cancel();
                _poller?.Stop();
                if (_loopTask is not null) await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error stopping broker.");
            }
            finally
            {
                Dispose();
                NetMQConfig.Cleanup();
            }
        }

        public void Dispose()
        {
            try
            {
                _poller?.Dispose();
                _router?.Dispose();
            }
            catch { /* best-effort */ }
        }

        // ————————————————————————————————————————————————————————————————————————
        // Main loop (single thread owns sockets + poller)
        // ————————————————————————————————————————————————————————————————————————
        private async Task RunAsync()
        {
            _router = new RouterSocket
            {
                Options =
                {
                    SendHighWatermark = _opts.SendHighWatermark,
                    ReceiveHighWatermark = _opts.ReceiveHighWatermark,
                    Linger = _opts.Linger,
                    RouterMandatory = true // fail fast on unknown identities
                }
            };
            _router.Bind(_opts.Bind);
            _log.LogInformation("Broker ROUTER bound at {Bind}", _opts.Bind);

            _router.ReceiveReady += OnReceive;

            _poller = new NetMQPoller { _router };

            _pruneTimer = new NetMQTimer(_opts.PruneInterval);
            _pruneTimer.Elapsed += (_, __) => Prune();
            _poller.Add(_pruneTimer);

            try
            {
                _poller.Run();
            }
            catch (TerminatingException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Broker poller crashed.");
            }

            await Task.CompletedTask;
        }

        // ————————————————————————————————————————————————————————————————————————
        // Receive/dispatch
        // ————————————————————————————————————————————————————————————————————————
        private void OnReceive(object? sender, NetMQSocketEventArgs e)
        {
            var router = (RouterSocket)e.Socket;

            NetMQMessage? rawMessage = null;
            while (router.TryReceiveMultipartMessage(ref rawMessage))
            {
                try
                {
                    // Expect: [Identity][Empty][HeaderJson][Payload?]
                    var wm = WireMessage.FromNetMQMessage(rawMessage);
                    var identityBytes = wm.GetIdentityBytes();
                    if (identityBytes == null)
                        throw new NullReferenceException("Dropping malformed message (no identity)");
                    var identityKey = Convert.ToBase64String(identityBytes);
                    // Presence: upsert + friendly name (if any)
                    if (string.IsNullOrEmpty(wm.Header.ServiceName))
                        throw new NullReferenceException("Failed to read service name");
                    _registry.Upsert(identityKey, wm.Header.Source, wm.Header.ServiceName);

                    if (wm.Header.Target == ServiceType.Broker)
                    {
                        HandleBrokerDirected(router, identityBytes, identityKey, wm);
                    }
                    else
                    {
                        Forward(router, identityBytes, identityKey, wm);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed processing inbound message.");
                }
            }
        }
        

        // Commands directed at the broker (heartbeats, queries, etc.)
        private void HandleBrokerDirected(RouterSocket router, byte[] replyToBytes, string replyToKey, WireMessage incoming)
        {
            switch (incoming.Header.Command)
            {
                case ControlCommand.Heartbeat:
                    ReplyJson(router, replyToBytes, incoming, ControlCommand.ServiceReply,
                        JsonSerializer.Serialize(new { ok = true, code = "heartbeat-ok" }));
                    break;

                case ControlCommand.ServiceRequest:
                    // Optional query payload: {"query":"list","type":"Worker"}
                    try
                    {
                        string? query = null, typeStr = null;
                        if (incoming.HasPayload && incoming.Header.PayloadType != PayloadType.RawBytes)
                        {
                            using var doc = JsonDocument.Parse(incoming.GetPayloadAsString() ?? "{}");
                            if (doc.RootElement.TryGetProperty("query", out var q)) query = q.GetString();
                            if (doc.RootElement.TryGetProperty("type", out var t)) typeStr = t.GetString();
                        }

                        if (string.Equals(query, "list", StringComparison.OrdinalIgnoreCase) &&
                            Enum.TryParse<ServiceType>(typeStr, true, out var tFilter))
                        {
                            var list = _registry.GetByType(tFilter)
                                .Select(r => new { r.Name, r.LastSeenUtc })
                                .ToArray();

                            ReplyJson(router, replyToBytes, incoming, ControlCommand.ServiceReply,
                                JsonSerializer.Serialize(new { ok = true, services = list }));
                        }
                        else
                        {
                            ReplyJson(router, replyToBytes, incoming, ControlCommand.ServiceReply,
                                JsonSerializer.Serialize(new { ok = true, code = "service-request-ok" }));
                        }
                    }
                    catch (Exception ex)
                    {
                        ReplyJson(router, replyToBytes, incoming, ControlCommand.ServiceReply,
                            JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                    }
                    break;

                default:
                    ReplyJson(router, replyToBytes, incoming, ControlCommand.ServiceReply,
                        JsonSerializer.Serialize(new { ok = false, code = "unknown-broker-command" }));
                    break;
            }
        }

        // Route to target or NACK if unroutable
        private void Forward(RouterSocket router, byte[] senderIdentity, string senderKey, WireMessage incoming)
        {
            var targetType = incoming.Header.Target;
            var stickyName = incoming.Header.Target == ServiceType.Worker ? incoming.Header.WorkerId : null;

            


            var dest = _chooser.Choose(_registry, targetType, stickyName);
            if (dest is null)
            {
                Nack(router, senderIdentity, incoming,
                    $"No online service for target '{targetType}'");
                return;
            }

            var destIdentity = GetIdentityBytes(dest.IdentityKey);
            if (destIdentity is null)
            {
                // In case registry has a record that we've never cached (unlikely), decode now
                try
                {
                    destIdentity = Convert.FromBase64String(dest.IdentityKey);
                    _idBytesCache[dest.IdentityKey] = destIdentity;
                }
                catch
                {
                    Nack(router, senderIdentity, incoming,
                        $"Invalid destination identity for '{dest.Name}'");
                    return;
                }
            }

            try
            {
                var outMsg = incoming.ToNetMQMessage(destIdentity);
                router.SendMultipartMessage(outMsg);
            }
            catch (HostUnreachableException)
            {
                // Destination vanished → prune and NACK
                _registry.Remove(dest.IdentityKey);
                _idBytesCache.TryRemove(dest.IdentityKey, out _);

                Nack(router, senderIdentity, incoming,
                    $"Destination '{dest.Name}' unreachable");
            }
            catch (Exception ex)
            {
                Nack(router, senderIdentity, incoming, ex.Message);
            }
        }

        private static void ReplyJson(RouterSocket router, byte[] replyTo, WireMessage cause,
            ControlCommand command, string json)
        {
            var hdr = new MessageHeader
            {
                Command = command,
                ServiceName = cause.Header.ServiceName,
                Source = ServiceType.Broker,
                Target = cause.Header.Source,
                PayloadType = PayloadType.Json,
                RequiresAck = false,
                CausationId = cause.Header.MessageId,
                CorrelationId = cause.Header.CorrelationId,
                JobId = cause.Header.JobId,
                WorkerId = cause.Header.WorkerId
            };

            var reply = new WireMessage(hdr, Encoding.UTF8.GetBytes(json));
            router.SendMultipartMessage(reply.ToNetMQMessage(replyTo));
        }

        private static void Nack(RouterSocket router, byte[] replyTo, WireMessage cause, string error)
        {
            var payload = JsonSerializer.Serialize(new
            {
                ok = false,
                error,
                target = cause.Header.Target.ToString(),
                correlationId = cause.Header.CorrelationId
            });

            ReplyJson(router, replyTo, cause, ControlCommand.PayloadNack, payload);
        }

        // ————————————————————————————————————————————————————————————————————————
        // Housekeeping
        // ————————————————————————————————————————————————————————————————————————
        private void Prune()
        {
            try
            {
                var removed = _registry.PruneStale(_opts.PresenceTtl);

                // Clean stale identities from the small cache too.
                foreach (var key in _idBytesCache.Keys)
                {
                    if (!_registry.TryGet(key, out _))
                        _idBytesCache.TryRemove(key, out _);
                }

                if (removed > 0)
                    _log.LogDebug("Pruned {Removed} stale services.", removed);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error during prune.");
            }
        }

        private byte[]? GetIdentityBytes(string identityKey)
        {
            if (_idBytesCache.TryGetValue(identityKey, out var cached))
                return cached;

            try
            {
                var bytes = Convert.FromBase64String(identityKey);
                _idBytesCache[identityKey] = bytes;
                return bytes;
            }
            catch { return null; }
        }

    }
}
