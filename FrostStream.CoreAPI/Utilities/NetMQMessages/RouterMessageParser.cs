using NetMQ;

namespace FrostStream.CoreAPI.Utilities.NetMQMessages;

public interface IRouterMessage
{
    string AgentId { get; }
    string Command { get; }
}

public static class RouterMessageParser
{
    public static IRouterMessage Parse(NetMQMessage msg)
    {
        if (msg.FrameCount < 3)
            throw new ArgumentException("Invalid message frame count");

        string agentId = msg[(int)RouterFrameIndex.AgentId].ConvertToString();
        string command = msg[(int)RouterFrameIndex.Command].ConvertToString().ToUpperInvariant();

        return command switch
        {
            "READY" => new ReadyMessage(agentId),
            "DONE" => new DoneMessage(agentId, ParseJobId(msg)),
            "FAILED" => new FailedMessage(agentId, ParseJobId(msg)),
            "JOB" => new JobMessage(agentId, ParseJobId(msg), ParsePayload(msg)),
            "PING" => new PingMessage(agentId),
            "DISCONNECT" => new DisconnectMessage(agentId),
            "WAIT" => new WaitMessage(agentId),
            "SHUTDOWN" => new ShutdownMessage(agentId),
            _ => throw new InvalidOperationException($"Unknown command: {command}")
        };
    }

    private static int ParseJobId(NetMQMessage msg)
    {
        if (msg.FrameCount < 4)
            throw new ArgumentException("Missing job id frame");

        if (!int.TryParse(msg[(int)RouterFrameIndex.JobId].ConvertToString(), out var jobId))
            throw new FormatException("Job id frame is not a valid integer");

        return jobId;
    }

    private static string ParsePayload(NetMQMessage msg)
    {
        if (msg.FrameCount < 5)
            throw new ArgumentException("Missing payload frame");

        return msg[(int)RouterFrameIndex.Payload].ConvertToString();
    }

    private enum RouterFrameIndex : int
    {
        AgentId = 0, // Agent ID
        Empty = 1, // NetMQ router socket empty frame
        Command = 2, // Command type (e.g., "READY", "JOB")
        JobId = 3, // Job ID (if applicable)
        Payload = 4 // Job payload (if applicable)
    }
}

// Concrete message types

public readonly struct ReadyMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "READY";

    public ReadyMessage(string agentId) => AgentId = agentId;
}

public readonly struct DoneMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "DONE";
    public int JobId { get; }

    public DoneMessage(string agentId, int jobId)
    {
        AgentId = agentId;
        JobId = jobId;
    }
}

public readonly struct FailedMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "FAILED";
    public int JobId { get; }

    public FailedMessage(string agentId, int jobId)
    {
        AgentId = agentId;
        JobId = jobId;
    }
}

public readonly struct JobMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "JOB";
    public int JobId { get; }
    public string Payload { get; }

    public JobMessage(string agentId, int jobId, string payload)
    {
        AgentId = agentId;
        JobId = jobId;
        Payload = payload;
    }
}

public readonly struct PingMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "PING";

    public PingMessage(string agentId) => AgentId = agentId;
}

public readonly struct DisconnectMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "DISCONNECT";

    public DisconnectMessage(string agentId) => AgentId = agentId;
}

public readonly struct WaitMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "WAIT";

    public WaitMessage(string agentId) => AgentId = agentId;
}

public readonly struct ShutdownMessage : IRouterMessage
{
    public string AgentId { get; }
    public string Command => "SHUTDOWN";

    public ShutdownMessage(string agentId) => AgentId = agentId;
}    
