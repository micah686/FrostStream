using System.Globalization;
using Shared.Messaging;
using Worker.Metadata;
using YtDlpSharpLib.Exceptions;

namespace Worker.Services;

internal static class YtDlpFailureDetails
{
    public static string DescribeException(Exception ex)
    {
        var ytDlpOutput = GetYtDlpOutput(ex);

        if (IsYtDlpSignInOrBotChallenge(ytDlpOutput))
        {
            return
                "yt-dlp could not access the source because the provider is requiring sign-in or bot verification. " +
                "For YouTube this usually means the Worker needs valid browser cookies configured for yt-dlp. " +
                $"Raw yt-dlp error: {ytDlpOutput}";
        }

        if (IsYtDlpAuthenticationFailure(ytDlpOutput))
        {
            return
                "yt-dlp could not access the source because the provider requires authentication. " +
                $"Raw yt-dlp error: {ytDlpOutput}";
        }

        return ytDlpOutput;
    }

    public static string? ErrorCode(Exception ex)
    {
        var text = GetYtDlpOutput(ex);

        if (IsYtDlpSignInOrBotChallenge(text))
            return "yt-dlp.sign-in-or-bot-verification-required";

        if (IsYtDlpAuthenticationFailure(text))
            return "yt-dlp.authentication-required";

        return ex switch
        {
            YtDlpPlaceholderContentException => PlaceholderContentDetector.ErrorCode,
            YtDlpUnavailableException => "yt-dlp.source-unavailable",
            YtDlpProcessException processException when processException.ExitCode is { } exitCode
                => $"yt-dlp.exit-{exitCode.ToString(CultureInfo.InvariantCulture)}",
            YtDlpException => "yt-dlp.failed",
            _ => null
        };
    }

    public static FailureKind ClassifyFailure(Exception ex)
        => ex switch
        {
            OperationCanceledException => FailureKind.Cancelled,
            TimeoutException => FailureKind.Timeout,
            YtDlpException ytDlpException => ClassifyYtDlpFailure(ytDlpException),
            _ => FailureKind.Transient
        };

    public static FailureKind ClassifyYtDlpFailure(YtDlpException ex)
    {
        var text = GetYtDlpOutput(ex);

        return ex is YtDlpUnavailableException
               || IsYtDlpAuthenticationFailure(text)
               || IsYtDlpSignInOrBotChallenge(text)
            ? FailureKind.Permanent
            : FailureKind.Transient;
    }

    private static string GetYtDlpOutput(Exception ex)
        => ex is YtDlpProcessException { LastStderrLines: { Length: > 0 } stderr }
            ? stderr
            : ex.Message;

    private static bool IsYtDlpSignInOrBotChallenge(string text)
        => Contains(text, "not a bot")
           || Contains(text, "confirm you are not")
           || Contains(text, "confirm you're not")
           || Contains(text, "sign in to confirm")
           || Contains(text, "use --cookies")
           || Contains(text, "use cookies from browser");

    private static bool IsYtDlpAuthenticationFailure(string text)
        => Contains(text, "login required")
           || Contains(text, "authentication required")
           || Contains(text, "sign in to")
           || Contains(text, "private video")
           || Contains(text, "members-only");

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
