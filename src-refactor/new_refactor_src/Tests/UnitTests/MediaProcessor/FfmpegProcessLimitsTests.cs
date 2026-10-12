using global::MediaProcessor;
using global::MediaProcessor.Ffmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
namespace UnitTests.MediaProcessor;

public sealed class FfmpegProcessLimitsTests
{
    [Test]
    public async Task Cancellation_Kills_Children_And_Queued_Cancellation_Does_Not_Start_A_Process()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runner = new FfmpegRunner(Options.Create(new MediaProcessorOptions { FfmpegPath = "/bin/sh", MaxConcurrentProcesses = 1 }), NullLogger<FfmpegRunner>.Instance);
        using var activeStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var active = runner.RunFfmpegAsync("-c \"sleep 30\"", null, activeStop.Token);
        for (var i = 0; i < 100 && runner.ActiveProcesses == 0; i++) await Task.Delay(10);
        runner.ActiveProcesses.ShouldBe(1);
        using var queuedStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunFfmpegAsync("-c \"exit 17\"", null, queuedStop.Token));
        runner.ActiveProcesses.ShouldBe(1);
        activeStop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => active);
        runner.ActiveProcesses.ShouldBe(0);
        await runner.RunFfmpegAsync("-c \"exit 0\"", null, default);
        runner.ActiveProcesses.ShouldBe(0);
    }
}
