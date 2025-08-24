using System.Text.Json;
using NetMQ;

namespace FrostStream.Shared;

public enum ControlCommand : byte
{
    Heartbeat, // alive check, no payload
    // WebAPI <-> Worker Commands
    JobDispatch,
    Ready, // Worker tells broker it's ready for a job
    ReadyAck,      // Broker confirms it got Ready
    CancelJob,
    ProgressUpdate,
    JobDone,

    // Worker <-> DataBridge Commands
    PayloadToDataBridge,
    PayloadAck, // DataBridge -> Worker ACK
    PayloadNack, // DataBridge -> Worker NACK
    
    // Internal Broker Commands / Other service comms
    ServiceRequest,
    ServiceReply
}

public sealed record WireMessage(
    ControlCommand Command,
    Guid JobId,
    string? WorkerId = null,
    byte[] Payload = null,
    Guid CorrelationId = default)
{
    // Init‑only property – no need for a separate field.
    public byte[] Payload { get; init; } =
        Payload ?? [];

    /// <summary>
    /// Factory that serializes an arbitrary POCO into the payload
    /// as UTF‑8 JSON bytes.
    /// </summary>
    public static WireMessage CreateWithJson<T>(
        ControlCommand command,
        Guid jobId,
        string? workerId = null,
        T jsonData = default!,
        Guid correlationId = default)
    {
        var payload = jsonData != null
            ? JsonSerializer.SerializeToUtf8Bytes(jsonData)
            : [];

        return new WireMessage(
            Command: command,
            JobId: jobId,
            WorkerId: workerId,
            Payload: payload,
            CorrelationId: correlationId == Guid.Empty ? Guid.NewGuid() : correlationId);
    }

    // NetMQ serialization
    public NetMQMessage ToNetMQMessage(byte[]? destinationIdentity = null)
    {
        var msg = new NetMQMessage();
        // The first frame is the destination identity, used by ROUTER sockets.
        // If it's null, we're sending from a client that doesn't specify the destination (e.g., worker sending to broker).
        // The broker will add the identity when forwarding.
        if (destinationIdentity != null)
        {
            msg.Append(destinationIdentity);
        }
        msg.AppendEmptyFrame();  // Required for ROUTER routing
        msg.Append([(byte)Command]);
        msg.Append(JobId.ToString());
        msg.Append(WorkerId ?? string.Empty);
        msg.Append(CorrelationId.ToString());
        msg.Append(Payload);
        return msg;
    }

    // NetMQ deserialization
    public static WireMessage FromNetMQMessage(NetMQMessage msg)
    {
        // This assumes the message has the structure:
        // [optional identity frame]
        // [empty frame]
        // [command]
        // [jobId]
        // [workerId]
        // [correlationId]
        // [payload]

        // Find the empty frame to start processing from the correct position
        int start = 0;
        for (int i = 0; i < msg.FrameCount; i++)
        {
            if (msg[i].IsEmpty)
            {
                start = i + 1;
                break;
            }
        }
        
        if (msg.FrameCount < start + 4)
        {
            throw new ArgumentException("Invalid message format: not enough frames.");
        }
        
        return new WireMessage(
            Command: (ControlCommand)msg[start++].Buffer[0],
            JobId: Guid.TryParse(msg[start++].ConvertToString(), out var jobId)? jobId : Guid.Empty,
            WorkerId: msg[start++].ConvertToString(),
            CorrelationId: Guid.TryParse(msg[start++].ConvertToString(), out var corrId) ? corrId : Guid.Empty,
            Payload: msg[start].ToByteArray()
        );
    }

    /// <summary>
    /// Deserializes the JSON payload into an instance of T.
    /// Returns default(T) if the payload is empty or cannot be parsed.
    /// </summary>
    public T GetJsonPayload<T>() =>
        Payload.Length > 0 ? JsonSerializer.Deserialize<T>(Payload)! : default!;
}