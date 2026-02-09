namespace Shared;

/// <summary>
/// NATS subject constants for inter-service messaging.
/// </summary>
public static class Subjects
{
    /// <summary>
    /// Subject for file processing requests, consumed by workers via queue group.
    /// </summary>
    public const string ProcessFile = "froststream.file.process";
}
