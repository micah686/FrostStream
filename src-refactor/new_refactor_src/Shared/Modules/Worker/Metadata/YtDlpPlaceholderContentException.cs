using YtDlpSharpLib.Exceptions;

namespace Worker.Metadata;

internal sealed class YtDlpPlaceholderContentException : YtDlpUnavailableException
{
    public YtDlpPlaceholderContentException(string message)
        : base(message)
    {
    }
}
