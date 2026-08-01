namespace YtDlpSharpLib.Downloads;

/// <summary>
/// Options for downloading the live-chat replay of a stream
/// (maps to <c>--write-subs --sub-langs live_chat --skip-download</c>).
/// </summary>
public record LiveChatDownloadOptions : DownloadOptions;
