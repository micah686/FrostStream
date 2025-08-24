using System.Collections.Concurrent;
using System.Text;
using System.IO;
using FrostStream.Shared;
using NetMQ.Sockets;
using NetMQ;
using Newtonsoft.Json;
using WatsonTcp;

namespace FrostStream.DataBridge;

/// <summary>
/// Listens for transfer reservation requests via the broker and
/// handles the actual Watson TCP file transfers.
/// </summary>
public class DataBridgeServer
{
    private readonly TransferCoordinator _coordinator = new();
    private readonly ConcurrentDictionary<Guid, TransferState> _transfers = new();
    private readonly string _storagePath = Path.Combine(Directory.GetCurrentDirectory(), "storage");
    private readonly int _port = 9000;

    private readonly DealerSocket _broker;
    private readonly WatsonTcpServer _server;

    public DataBridgeServer()
    {
        Directory.CreateDirectory(_storagePath);

        _broker = new DealerSocket(">tcp://localhost:5557");

        _server = new WatsonTcpServer("127.0.0.1", _port);
        _server.Events.ClientConnected += (s, e) =>
        {
            _transfers[e.Client.Guid] = new TransferState();
        };
        _server.Events.ClientDisconnected += (s, e) =>
        {
            _transfers.TryRemove(e.Client.Guid, out _);
        };
        _server.Events.MessageReceived += MessageReceived;
    }

    public void Start()
    {
        _server.Start();
        Task.Run(BrokerLoop);
    }

    private void BrokerLoop()
    {
        while (true)
        {
            var msg = _broker.ReceiveMultipartMessage();
            var wire = WireMessage.FromNetMQMessage(msg);

            if (wire.Command == ControlCommand.TransferReserve)
            {
                var req = wire.GetJsonPayload<TransferRequest>();
                var reply = _coordinator.Reserve(req.JobId, req.WorkerId, req.SizeBytes);

                WireMessage resp = reply switch
                {
                    TransferGranted g => WireMessage.CreateWithJson(ControlCommand.TransferGranted, req.JobId, req.WorkerId, g),
                    TransferDenied d => WireMessage.CreateWithJson(ControlCommand.TransferDenied, req.JobId, req.WorkerId, d),
                    _ => throw new InvalidOperationException()
                };

                _broker.SendMultipartMessage(resp.ToNetMQMessage());
            }
        }
    }

    private void MessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        if (!_transfers.TryGetValue(e.Client.Guid, out var state))
            return;

        if (e.Metadata != null && e.Metadata.ContainsKey(TransferMessage.MetaData.ToString()))
        {
            state.JsonStream ??= new MemoryStream();
            state.JsonStream.Write(e.Data, 0, e.Data.Length);

            if (e.Metadata.ContainsKey(TransferMessage.MetaData_EOF.ToString()))
            {
                state.JsonStream.Position = 0;
                using var reader = new StreamReader(state.JsonStream, Encoding.UTF8);
                var json = reader.ReadToEnd();
                state.JsonStream.Dispose();

                state.Metadata = JsonConvert.DeserializeObject<FileTransferMetadata>(json);
                if (state.Metadata == null)
                    return;

                if (!_coordinator.TryBegin(state.Metadata.LeaseId, state.Metadata.WorkerId))
                {
                    SendNack(state.Metadata, "Invalid lease");
                    return;
                }

                // verify storage space
                var drive = new DriveInfo(Path.GetPathRoot(_storagePath)!);
                if ((long)state.Metadata.TotalSizeBytes > drive.AvailableFreeSpace)
                {
                    SendNack(state.Metadata, "Insufficient storage");
                    _coordinator.Cancel(state.Metadata.LeaseId);
                    return;
                }

                state.TempPath = Path.Combine(_storagePath, $"{Guid.NewGuid()}.tmp");
                state.FileStream = new FileStream(state.TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
        }
        else if (state.Metadata != null)
        {
            // file data
            state.FileStream ??= new FileStream(state.TempPath!, FileMode.Append, FileAccess.Write, FileShare.None);
            state.FileStream.Write(e.Data, 0, e.Data.Length);
            state.Received += (ulong)e.Data.Length;
            _coordinator.UpdateActivity(state.Metadata.LeaseId);

            if (e.Metadata != null && e.Metadata.ContainsKey(TransferMessage.File_EOF.ToString()))
            {
                state.FileStream.Flush();
                state.FileStream.Dispose();

                var finalPath = Path.Combine(_storagePath, state.Metadata.FileName);
                File.Move(state.TempPath!, finalPath, true);

                _coordinator.Complete(state.Metadata.LeaseId, true);
                SendAck(state.Metadata);
                _transfers.TryRemove(e.Client.Guid, out _);
            }
        }
    }

    private void SendAck(FileTransferMetadata meta)
    {
        var msg = new WireMessage(ControlCommand.PayloadAck, meta.JobId, meta.WorkerId);
        _broker.SendMultipartMessage(msg.ToNetMQMessage());
    }

    private void SendNack(FileTransferMetadata meta, string reason)
    {
        var msg = WireMessage.CreateWithJson(ControlCommand.PayloadNack, meta.JobId, meta.WorkerId, new { reason });
        _broker.SendMultipartMessage(msg.ToNetMQMessage());
    }
}

internal class TransferState
{
    public MemoryStream? JsonStream { get; set; }
    public FileTransferMetadata? Metadata { get; set; }
    public FileStream? FileStream { get; set; }
    public string? TempPath { get; set; }
    public ulong Received { get; set; }
}
