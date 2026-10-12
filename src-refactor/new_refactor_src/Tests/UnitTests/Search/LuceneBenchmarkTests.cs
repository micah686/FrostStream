using System.Diagnostics;
using System.Text.Json;
using DataBridge.Search;
using FrostStream.Lite.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

/// <summary>Opt-in reproducible development measurement; not a substitute for the 8 GB laptop gate.</summary>
public sealed class LuceneBenchmarkTests
{
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
                    peakProcessRssBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                    measurementsNotIncluded = new[] { "8 GB physical machine", "cold filesystem cache", "15-minute idle", "60-minute combined download/transcode/index", "frontend/POT/FFmpeg memory", "search disk full" }
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
