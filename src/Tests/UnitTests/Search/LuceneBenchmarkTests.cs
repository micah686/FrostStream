using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using DataBridge.Search;
using FrostStream.Lite.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

/// <summary>Opt-in reproducible development measurement; not a substitute for the 8 GB laptop gate.</summary>
public sealed class LuceneBenchmarkTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(int fd, long offset, long length, int advice);

    [Test]
    public async Task Reference_Corpus_Measurement()
    {
        var output = Environment.GetEnvironmentVariable("FROSTSTREAM_LUCENE_BENCHMARK_REPORT"); if (output is null) return;
        var path = Path.Combine(Path.GetTempPath(), "lucene-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = Stopwatch.StartNew();
            var options = new LuceneSearchOptions { DirectoryPath = path };
            long corpusBytes = 0;
            using (var service = new LuceneSearchService(options, NullLogger<LuceneSearchService>.Instance))
            {
                await service.EnsureAllCollectionsAsync();
                await using (var generation = await service.CreateGenerationAsync())
                {
                    for (var offset = 0; offset < 10_000; offset += 500)
                    {
                        var batch = Enumerable.Range(offset, 500).Select(i => LuceneLifecycleTests.Media($"Reference video {i} café graphics", i.ToString("D32"))).ToArray();
                        corpusBytes += batch.Sum(x => JsonSerializer.SerializeToUtf8Bytes(x).LongLength);
                        await generation.Writer.BulkImportMediaAsync(batch);
                    }
                    for (var offset = 0; offset < 100_000; offset += 500)
                    {
                        var batch = Enumerable.Range(offset, 500).Select(i => new CaptionDocument { Id = "caption-" + i, MediaGuid = (i % 10_000).ToString("D32"), LanguageCode = "en", CaptionType = "manual", StoragePath = "track-" + i + ".vtt", Text = $"Reference transcript {i} discussing graphics engines and Unicode café 日本語. Searchable caption content for the reproducible development workload." }).ToArray();
                        corpusBytes += batch.Sum(x => JsonSerializer.SerializeToUtf8Bytes(x).LongLength);
                        await generation.Writer.BulkImportCaptionsAsync(batch);
                    }
                    for (var offset = 0; offset < 250_000; offset += 500)
                    {
                        var batch = Enumerable.Range(offset, 500).Select(i => new CommentDocument { Id = "comment-" + i, MediaGuid = (i % 10_000).ToString("D32"), ParentCommentId = "", Text = $"Reference comment {i} about graphics and Unicode café 日本語 with repeatable content.", Platform = "youtube", AccountName = "Commenter", AccountHandle = "commenter", CommentTimestampUnix = i, LikeCount = i % 100 }).ToArray();
                        corpusBytes += batch.Sum(x => JsonSerializer.SerializeToUtf8Bytes(x).LongLength);
                        await generation.Writer.BulkImportCommentsAsync(batch);
                    }
                    await generation.ActivateAsync();
                }
                var indexingSeconds = clock.Elapsed.TotalSeconds;
                var latency = new List<double>();
                for (var i = 0; i < 100; i++)
                {
                    clock.Restart();
                    var page = await service.SearchAsync<MediaDocument>(SearchCollection.Media, new("graphics", "title") { SortBy = new("added_at_sort", true) });
                    page.Found.ShouldBe(10_000); latency.Add(clock.Elapsed.TotalMilliseconds);
                }
                var counts = new Dictionary<string, int>();
                foreach (var collection in Enum.GetValues<SearchCollection>()) counts[collection.ToString()] = await service.GetDocumentCountAsync(collection.ToString());
                var incremental = LuceneLifecycleTests.Media("incremental committed update", "0".PadLeft(32, '0'));
                clock.Restart(); await service.UpsertMediaAsync(incremental); var incrementalMilliseconds = clock.Elapsed.TotalMilliseconds;
                latency.Sort();
                clock.Restart();
                // Commit and close are included before the restart measurement.
                service.Dispose();
                using var reopened = new LuceneSearchService(options, NullLogger<LuceneSearchService>.Instance);
                (await reopened.EnsureAllCollectionsAsync()).ShouldBeFalse();
                var reopenSeconds = clock.Elapsed.TotalSeconds;
                // Close mappings before asking Linux to evict this disposable index's data pages.
                // This does not flush the host's global cache or measure cold metadata caches.
                reopened.Dispose();
                var coldLatency = new List<double>();
                for (var sample = 0; sample < 20; sample++)
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        posix_fadvise(handle.DangerousGetHandle().ToInt32(), 0, 0, 4).ShouldBe(0);
                    }
                    using var cold = new LuceneSearchService(options, NullLogger<LuceneSearchService>.Instance);
                    await cold.EnsureAllCollectionsAsync();
                    clock.Restart();
                    var page = await cold.SearchAsync<MediaDocument>(SearchCollection.Media, new("graphics", "title") { SortBy = new("added_at_sort", true) });
                    page.Found.ShouldBe(9_999);
                    coldLatency.Add(clock.Elapsed.TotalMilliseconds);
                }
                coldLatency.Sort();
                using var idle = new LuceneSearchService(options, NullLogger<LuceneSearchService>.Instance);
                await idle.EnsureAllCollectionsAsync();
                var idleSeconds = int.Parse(Environment.GetEnvironmentVariable("FROSTSTREAM_BENCHMARK_IDLE_SECONDS") ?? "0");
                using var process = Process.GetCurrentProcess();
                var idleCpuBefore = process.TotalProcessorTime.TotalSeconds;
                clock.Restart();
                if (idleSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
                var idleElapsedSeconds = clock.Elapsed.TotalSeconds;
                var idleCpuSeconds = process.TotalProcessorTime.TotalSeconds - idleCpuBefore;
                var report = new
                {
                    scope = "Development Lucene-only synthetic corpus; not an 8 GB full-system acceptance run",
                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    logicalCpus = Environment.ProcessorCount,
                    hostMemory = OperatingSystem.IsLinux() ? File.ReadLines("/proc/meminfo").First() : "unavailable",
                    counts, corpusBytes, indexBytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length),
                    ramBufferMiB = 16, indexingSeconds, reopenSeconds, incrementalMilliseconds,
                    warmSearchP50Milliseconds = latency[49], warmSearchP95Milliseconds = latency[94], warmSearchP99Milliseconds = latency[98],
                    coldSearch = new { samples = coldLatency.Count, p50Milliseconds = coldLatency[9], p95Milliseconds = coldLatency[18], p99Milliseconds = coldLatency[19], method = "Linux POSIX_FADV_DONTNEED before opening service; query timed after initialization; metadata caches remain warm; advisory eviction is not guaranteed" },
                    idle = new { requestedSeconds = idleSeconds, elapsedSeconds = idleElapsedSeconds, processCpuSeconds = idleCpuSeconds, percentOneCore = idleSeconds > 0 ? idleCpuSeconds / idleElapsedSeconds * 100 : (double?)null },
                    peakProcessRssBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                    measurementsNotIncluded = new[] { "8 GB physical machine", "verified cold filesystem and metadata cache", "full-system idle", "60-minute combined download/transcode/index", "frontend/POT/FFmpeg memory", "search disk full" }
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
