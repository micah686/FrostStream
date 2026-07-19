using System.Globalization;
using System.Linq;
using Shared.Messaging;
using Worker.Metadata;
using YtDlpSharpLib.Exceptions;

namespace Worker.Services;

internal static class YtDlpFailureDetails
{
    public static string DescribeException(Exception ex, string? provider = null, string? sourceUrl = null)
    {
        var ytDlpOutput = GetYtDlpOutput(ex);

        if (ClassifyProviderAccessFailure(ex, provider, sourceUrl) is { } providerFailure)
            return providerFailure.Description;

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

    public static string? ErrorCode(Exception ex, string? provider = null, string? sourceUrl = null)
    {
        if (ClassifyProviderAccessFailure(ex, provider, sourceUrl) is { } providerFailure)
            return providerFailure.ErrorCode;

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

    public static ProviderAccessFailure? ClassifyProviderAccessFailure(
        Exception ex,
        string? provider = null,
        string? sourceUrl = null)
    {
        var normalizedProvider = ResolveProvider(provider, sourceUrl);
        if (normalizedProvider is null)
            return null;

        var text = GetYtDlpOutput(ex);
        return normalizedProvider switch
        {
            "youtube" when IsYtDlpBotChallenge(text) => ProviderAccessFailure.BotDetection(
                normalizedProvider,
                "yt-dlp.youtube.bot-detection-halted",
                "yt-dlp could not access YouTube because YouTube is requiring bot verification. " +
                "The persistent YouTube provider circuit has been opened until an administrator clears it. " +
                $"Raw yt-dlp error: {text}"),
            _ => null
        };
    }

    public static string? ResolveProvider(string? provider = null, string? sourceUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var normalized = provider.Trim().ToLowerInvariant();
            if (normalized.StartsWith("youtube", StringComparison.Ordinal))
                return "youtube";
            return normalized.Split(':', 2)[0];
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        return host switch
        {
            "youtu.be" => "youtube",
            "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" => "youtube",
            _ when host.EndsWith(".youtube.com", StringComparison.Ordinal) => "youtube",
            _ => null
        };
    }

    /// <summary>
    /// True when yt-dlp aborted (via <c>--abort-on-error</c>) purely because an optional sidecar
    /// asset — subtitles or the thumbnail — failed to download, not because the requested video/audio
    /// content itself failed. When primary media exists, V2 settles this as an optional-artifact
    /// warning without hiding another yt-dlp invocation inside the same application attempt. Only
    /// matches when the *entire* stderr tail is sidecar-only chatter, so a genuine content failure
    /// that happens to also mention subtitles is not misclassified.
    /// </summary>
    public static bool IsSidecarOnlyFailure(Exception ex)
    {
        if (ex is not YtDlpProcessException { LastStderrLines: { Length: > 0 } stderr })
            return false;

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return false;

        return lines.All(IsSidecarChatterLine);
    }

    private static bool IsSidecarChatterLine(string line)
        => Contains(line, "unable to download video subtitles")
           || Contains(line, "unable to write video subtitles")
           || Contains(line, "unable to download video thumbnail")
           || Contains(line, "unable to write video thumbnail")
           // Non-fatal informational/progress lines that can precede the fatal ERROR line above.
           || !Contains(line, "error");

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
        => IsYtDlpBotChallenge(text)
           || Contains(text, "use --cookies")
           || Contains(text, "use cookies from browser");

    private static bool IsYtDlpBotChallenge(string text)
        => Contains(text, "not a bot")
           || Contains(text, "confirm you are not")
           || Contains(text, "confirm you're not")
           || Contains(text, "sign in to confirm");

    private static bool IsYtDlpAuthenticationFailure(string text)
        => Contains(text, "login required")
           || Contains(text, "authentication required")
           || Contains(text, "sign in to")
           || Contains(text, "private video")
           || Contains(text, "members-only");

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ProviderAccessFailure(
    string Provider,
    string ErrorCode,
    string Description,
    bool HaltProviderDownloads)
{
    public static ProviderAccessFailure BotDetection(string provider, string errorCode, string description)
        => new(provider, errorCode, description, HaltProviderDownloads: true);
}
