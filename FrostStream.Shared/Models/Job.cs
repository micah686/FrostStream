namespace FrostStream.Shared.Models;

public class Job
{
    public int Id { get; set; } // LiteDB auto-increment
    public Guid JobGuid { get; set; } = Guid.Empty; // stable external GUID
    public string Payload { get; set; }
    public JobStatus Status { get; set; } // Pending, InProgress, Done, Failed
    public string AssignedAgent { get; set; }
    public int RetryCount { get; set; } = 0;
    public int MaxRetries { get; set; } = 3;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTriedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; } // for backoff scheduling
}
public enum JobStatus
{
    Pending,
    InProgress,
    Done,
    Failed
}