namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Progress event raised while a binary is being downloaded.
/// </summary>
public sealed record BinaryDownloadProgress
{
    /// <summary>Which binary is being downloaded.</summary>
    public required BinaryKind Kind { get; init; }

    /// <summary>The URL currently being read.</summary>
    public required string Url { get; init; }

    /// <summary>Bytes received from the network so far.</summary>
    public long BytesReceived { get; init; }

    /// <summary>Total bytes when the server reports a <c>Content-Length</c>; otherwise <see langword="null"/>.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Fraction complete (0..1) when the total is known; otherwise <see langword="null"/>.</summary>
    public double? Fraction =>
        TotalBytes is { } total && total > 0
            ? Math.Min(1.0, (double)BytesReceived / total)
            : null;
}
