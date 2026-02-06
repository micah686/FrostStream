namespace Shared.Messages;

/// <summary>
/// Request from Worker to DataBridge asking which storage method to use.
/// </summary>
public record StorageConfigRequest
{
    /// <summary>
    /// The job ID this request is for.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Worker identifier making the request.
    /// </summary>
    public required string WorkerId { get; init; }
}
