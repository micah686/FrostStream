using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public enum ServiceType : byte
{
    None,
    Broker,
    WebApi,
    DataBridge,
    Worker
}

public enum PayloadType
{
    String,
    Json,
    RawBytes
}

public sealed class MessageHeader
{
    public byte Version { get; init; } = 1;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ControlCommand Command { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ServiceType Source { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ServiceType Target { get; set; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PayloadType PayloadType { get; set; }
    public bool RequiresAck { get; set; } = false;
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public Guid CausationId { get; set; }

    // Optional, used only from Worker.
    public Guid JobId { get; init; }
    public string? WorkerId { get; init; }
}

public sealed record WireMessage2(MessageHeader Header, byte[]? Payload = null)
{
    // Optional: You can add a constructor if needed
    public WireMessage2() : this(new MessageHeader()) { }

    public NetMQMessage ToNetMQMessage(byte[]? destinationIdentity = null)
    {
        var msg = new NetMQMessage();

        // Append destination identity if provided
        if (destinationIdentity != null)
        {
            msg.Append(destinationIdentity);
        }

        msg.AppendEmptyFrame(); // Required for ROUTER routing

        // Serialize header using System.Text.Json
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
            // Ensure DateTime is serialized in UTC
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var headerJson = JsonSerializer.Serialize(Header, options);
        msg.Append(headerJson, Encoding.UTF8);

        // Append payload if present
        if (Payload != null && Payload.Length > 0)
        {
            msg.Append(Payload);
        }

        return msg;
    }

    public static WireMessage2 FromNetMQMessage(NetMQMessage msg)
    {
        // Find the empty frame (delimiter)
        int delimIndex = -1;
        for (int i = 0; i < msg.FrameCount; i++)
        {
            if (msg[i].IsEmpty)
            {
                delimIndex = i;
                break;
            }
        }

        if (delimIndex < 0 || msg.FrameCount < delimIndex + 2)
            throw new ArgumentException("Invalid message framing (delimiter/header missing).");

        int headerIndex = delimIndex + 1;
        string headerJson = msg[headerIndex].ConvertToString(Encoding.UTF8);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var header = JsonSerializer.Deserialize<MessageHeader>(headerJson, options)
            ?? throw new InvalidOperationException("Failed to deserialize header.");

        byte[]? payload = null;
        int payloadIndex = headerIndex + 1;
        if (msg.FrameCount > payloadIndex)
        {
            payload = msg[payloadIndex].ToByteArray();
        }

        return new WireMessage2(header, payload);
    }

    public string? GetPayloadAsString()
    {
        if (Payload == null || Payload.Length == 0)
            return null;

        if (Header.PayloadType == PayloadType.RawBytes)
            throw new InvalidOperationException("Invalid Payload Type: Cannot convert RawBytes to string.");

        return Encoding.UTF8.GetString(Payload);
    }

    public T GetPayloadAsJson<T>()
    {
        if (Header.PayloadType == PayloadType.RawBytes)
            throw new InvalidOperationException("Invalid Payload Type: Cannot deserialize RawBytes as JSON.");

        var json = GetPayloadAsString();
        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException("No payload data present.");

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new InvalidOperationException("Deserialized JSON was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Unable to deserialize payload as the specified type.", ex);
        }
    }

    public byte[] GetPayloadAsBytes()
    {
        if (Payload == null || Payload.Length == 0)
            throw new InvalidOperationException("No payload data present.");

        return Payload;
    }

    public bool HasPayload => Payload is { Length: > 0 };
}