namespace Shared.Messaging;

public static class MediaToolsSubjects
{
    public const string Update = "media-tools.update";
}

public sealed record MediaToolsUpdateRequest;
public sealed record MediaToolsUpdateResponse(bool Success, string Message);
