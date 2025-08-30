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
    }
    internal class BrokerHostedService : IHostedService
    {
        private readonly ILogger<BrokerHostedService> _log;
        private readonly ServiceRegistry _registry;
        private readonly BrokerOptions _opts;
        // >>> Inject the scheduler (plain singleton, not a hosted service)
        private readonly JobScheduler _scheduler;

        private RouterSocket? _router;
        private NetMQPoller? _poller;
        private Task? _loopTask;
        private readonly CancellationTokenSource _cts = new();
        private NetMQTimer? _schedulerTimer;

        public BrokerHostedService(
            ILogger<BrokerHostedService> log,
            ServiceRegistry registry,
            JobScheduler scheduler)
        {
            _log = log;
            _registry = registry;
            _scheduler = scheduler;
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


            // If you had a prune timer before, remove it and let ServiceRegistryCleanup handle pruning.

            // >>> Add a scheduler tick on the NetMQ thread
            _schedulerTimer = new NetMQTimer(TimeSpan.FromSeconds(5));      // >>>
            _schedulerTimer.Elapsed += (_, __) =>                           // >>>
            {                                                               // >>>
                // 1) Reconcile with ServiceRegistry membership (handles workers evicted by cleanup)
                _scheduler.ReconcileRegistryAndRequeue();                   // >>>
                // 2) Attempt to dispatch due pending jobs to idle workers
                _scheduler.RequeueDueJobs(SendToWorker);                    // >>>
            };                                                              // >>>
            _poller.Add(_schedulerTimer);

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
                    _registry.Upsert(wm.Header.ServiceName, wm.Header.Source, identityBytes);

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

        // =========================================================================
        // Delegate that the scheduler uses to actually send to a worker
        // (Runs on the same NetMQ thread as the router/poller.)
        // =========================================================================
        private bool SendToWorker(string workerId, WireMessage message)
        {
            if (_router is null) return false;

            // Resolve identity fresh from ServiceRegistry each time
            if (!_registry.TryGetIdentity(workerId, out var identity) || identity.Length == 0)
            {
                _log.LogWarning("Cannot send to {WorkerId}: no identity in registry.", workerId);
                return false;
            }

            try
            {
                _router.SendMultipartMessage(message.ToNetMQMessage(identity));
                _log.LogDebug("Sent {Command} to {WorkerId}.", message.Header.Command, workerId);
                return true;
            }
            catch (HostUnreachableException)
            {
                // With ServiceRegistryCleanup in place, do NOT remove here; just log.
                _log.LogWarning("Worker {WorkerId} unreachable; will rely on cleanup to evict.", workerId);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error sending to {WorkerId}.", workerId);
                return false;
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
                
                default:
                    ReplyJson(router, replyToBytes, incoming, ControlCommand.ServiceReply,
                        JsonSerializer.Serialize(new { ok = false, code = "unknown-broker-command" }));
                    break;
            }
        }

        // Route to target or NACK if unroutable
        private void Forward(RouterSocket router, byte[] senderIdentity, string senderKey, WireMessage incoming)
        {
            
            
            var target = incoming.Header.Target;
            var services = _registry.GetByType(target);

            ServiceRecord? record = null;

            switch (target)
            {
                case ServiceType.WebApi:
                case ServiceType.DataBridge:
                    if (services.Count == 0)
                        throw new NullReferenceException($"Failed to find service for {target}");
                    if (services.Count >= 1)
                        throw new InvalidOperationException($"Multiple {target}s services is not supported");
                    //1 record
                    record = services[0];
                    break;
                case ServiceType.Broker:
                    break;
                case ServiceType.None:
                    break;
                case ServiceType.Worker:
                    if (services.Count == 0)
                        throw new NullReferenceException($"Failed to find service for {target}");
                    //Do processing to get the right worker(LRU)
                    //need to get friendly name
                    break;
                default:
                    break;
            }

            if(record == null )
                throw new NullReferenceException("Failed to find service");
            if(record.Identity is null || record.Identity.Length == 0)
                Nack(router, senderIdentity, incoming,
                    $"Destination '{record.ServiceName}' has no ROUTER identity yet");
            else
            {
                try
                {
                    var outMsg = incoming.ToNetMQMessage(record.Identity);
                    router.SendMultipartMessage(outMsg);
                }
                catch (HostUnreachableException)
                {
                    // Identity went stale; NACK
                    Nack(router, senderIdentity, incoming,
                        $"Destination '{record.ServiceName}' unreachable");
                }
                catch (Exception ex)
                {
                    Nack(router, senderIdentity, incoming, ex.Message);
                }
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

        
    }
}
