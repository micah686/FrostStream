namespace YtDlpSharpLib;

/// <summary>Non-throwing result for yt-dlp operations that do not return data.</summary>
public sealed record RunResult
{
    private RunResult(bool success, string errorOutput)
    {
        Success = success;
        ErrorOutput = errorOutput;
    }

    /// <summary>Whether the operation completed successfully.</summary>
    public bool Success { get; }

    /// <summary>Captured error output, or an error message for non-process failures.</summary>
    public string ErrorOutput { get; }

    /// <summary>Creates a successful result.</summary>
    public static RunResult Succeeded() =>
        new(success: true, errorOutput: string.Empty);

    /// <summary>Creates a failed result.</summary>
    public static RunResult Failed(string errorOutput) =>
        new(success: false, errorOutput);
}

/// <summary>Non-throwing result for yt-dlp operations that return data.</summary>
public sealed record RunResult<T>
{
    private RunResult(bool success, string errorOutput, T? data)
    {
        Success = success;
        ErrorOutput = errorOutput;
        Data = data;
    }

    /// <summary>Whether the operation completed successfully.</summary>
    public bool Success { get; }

    /// <summary>Captured error output, or an error message for non-process failures.</summary>
    public string ErrorOutput { get; }

    /// <summary>The operation data when <see cref="Success"/> is true; otherwise <see langword="default"/>.</summary>
    public T? Data { get; }

    /// <summary>Creates a successful result.</summary>
    public static RunResult<T> Succeeded(T data) =>
        new(success: true, errorOutput: string.Empty, data);

    /// <summary>Creates a failed result.</summary>
    public static RunResult<T> Failed(string errorOutput) =>
        new(success: false, errorOutput, data: default);
}
