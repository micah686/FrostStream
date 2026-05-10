using NodaTime;

namespace Shared.Messaging;

public sealed class OptionPresetDto
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>JSON-serialized <c>YtDlpSharpLib.Options.YtDlpOptions</c>.</summary>
    public required string YtDlpOptionsJson { get; init; }

    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed class OptionPresetCreateRequestMessage
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string YtDlpOptionsJson { get; init; }
}

public sealed class OptionPresetUpdateRequestMessage
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string YtDlpOptionsJson { get; init; }
}

public sealed class OptionPresetGetRequestMessage
{
    public required string Key { get; init; }
}

public sealed class OptionPresetListRequestMessage;

public sealed class OptionPresetDeleteRequestMessage
{
    public required string Key { get; init; }
}

public sealed class OptionPresetOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public OptionPresetDto? Entity { get; init; }
    public IReadOnlyList<OptionPresetDto>? Items { get; init; }
}
