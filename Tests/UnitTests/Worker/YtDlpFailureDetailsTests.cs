using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using Worker.Services;
using YtDlpSharpLib.Exceptions;

namespace UnitTests.Worker;

public sealed class YtDlpFailureDetailsTests
{
    [Test]
    public void Authentication_Challenge_Is_Reported_As_Permanent_With_Specific_Code()
    {
        var exception = new YtDlpProcessException(
            "yt-dlp failed",
            "yt-dlp https://example.test",
            1,
            "ERROR: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies");

        YtDlpFailureDetails.ClassifyFailure(exception).ShouldBe(FailureKind.Permanent);
        YtDlpFailureDetails.ErrorCode(exception)
            .ShouldBe("yt-dlp.sign-in-or-bot-verification-required");
        YtDlpFailureDetails.DescribeException(exception)
            .ShouldContain("requiring sign-in or bot verification");
    }

    [Test]
    public void Unavailable_YtDlp_Exception_Is_Permanent_Source_Unavailable()
    {
        var exception = new YtDlpUnavailableException("This video is unavailable");

        YtDlpFailureDetails.ClassifyFailure(exception).ShouldBe(FailureKind.Permanent);
        YtDlpFailureDetails.ErrorCode(exception).ShouldBe("yt-dlp.source-unavailable");
    }

    [Test]
    public void Process_Exception_With_Exit_Code_Uses_Exit_Code_Error_Code()
    {
        var exception = new YtDlpProcessException(
            "yt-dlp failed",
            "yt-dlp https://example.test",
            42,
            "temporary extractor failure");

        YtDlpFailureDetails.ClassifyFailure(exception).ShouldBe(FailureKind.Transient);
        YtDlpFailureDetails.ErrorCode(exception).ShouldBe("yt-dlp.exit-42");
    }

    [Test]
    public void Timeout_And_Cancellation_Are_Classified_Distinctly()
    {
        YtDlpFailureDetails.ClassifyFailure(new TimeoutException("timeout"))
            .ShouldBe(FailureKind.Timeout);
        YtDlpFailureDetails.ClassifyFailure(new OperationCanceledException())
            .ShouldBe(FailureKind.Cancelled);
    }
}
