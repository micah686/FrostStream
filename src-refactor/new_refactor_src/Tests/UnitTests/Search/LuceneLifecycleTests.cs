using System.Diagnostics;
using System.Text.Json;
using DataBridge.Search;
using FrostStream.Lite.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

public sealed class LuceneLifecycleTests
{
    private static LuceneSearchService Open(string path) => new(new LuceneSearchOptions { DirectoryPath = path }, NullLogger<LuceneSearchService>.Instance);
    internal static MediaDocument Media(string title, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), Title = title, Platform = "youtube", AccountId = 1,
        AccountName = "Channel", AccountHandle = "channel", DurationSeconds = 12.5, AddedAtSort = 100,
        ReleaseDateSort = 100, ViewCount = 100, VideoCodec = "hevc", CaptionLanguages = ["ja", "en"], Tags = ["tag with space"]
    };
    private static async Task Activate(LuceneSearchService service, params MediaDocument[] docs)
    {
        await using var generation = await service.CreateGenerationAsync();
        await generation.Writer.BulkImportMediaAsync(docs); await generation.ActivateAsync();
    }
    [Test]
    public async Task Durable_Updates_Reopen_Idempotently_And_Ties_Page_Stably()
    {
        var path = Path.Combine(Path.GetTempPath(), "lucene-reopen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Media("Unicode 日本語 café"); var second = Media("Unicode 日本語 café");
            using (var service = Open(path))
            {
                (await service.EnsureAllCollectionsAsync()).ShouldBeTrue();
                await Activate(service, first, second);
                await service.UpsertMediaAsync(first); await service.UpsertMediaAsync(first);
                var ids = new List<string>();
                for (var page = 1; page <= 2; page++)
                {
                    var result = await service.SearchAsync<MediaDocument>(SearchCollection.Media,
                        new SearchRequest("café", "title") { Page = page, PerPage = 1, SortBy = new("added_at_sort", true) });
                    result.Found.ShouldBe(2); ids.Add(result.Hits.Single().Document.Id);
                }
                ids.ShouldBe(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal).ToArray());
                var filter = new SearchFilter.All([new SearchFilter.Number("duration_seconds", Comparison.Greater, 12), new SearchFilter.Text("caption_languages", "ja")]);
                (await service.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title") { Filter = filter })).Found.ShouldBe(2);
                await Should.ThrowAsync<ArgumentOutOfRangeException>(() => service.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title") { Page = int.MaxValue }));
                // Long transcripts must never become single keyword terms (Lucene's term limit is 32 KiB).
                await service.BulkImportCaptionsAsync([new CaptionDocument { Id = "caption", MediaGuid = first.Id, LanguageCode = "en", CaptionType = "manual", StoragePath = "track.vtt", Text = string.Concat(Enumerable.Repeat("long transcript text ", 5000)) }]);
            }
            using (var reopened = Open(path))
            {
                (await reopened.EnsureAllCollectionsAsync()).ShouldBeFalse();
                (await reopened.SearchAsync<MediaDocument>(SearchCollection.Media, new("日本語", "title"))).Found.ShouldBe(2);
                (await reopened.SearchAsync<CaptionDocument>(SearchCollection.Captions, new("transcript", "text"))).Found.ShouldBe(1);
                await reopened.DeleteMediaByGuidAsync(first.Id);
            }
            using var again = Open(path);
            (await again.EnsureAllCollectionsAsync()).ShouldBeFalse();
            (await again.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title"))).Found.ShouldBe(1);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Test]
    public async Task Missing_Corrupt_And_Incompatible_Indexes_Request_Authoritative_Rebuild()
    {
        foreach (var failure in new[] { "missing", "corrupt", "schema" })
        {
            var path = Path.Combine(Path.GetTempPath(), "lucene-recovery-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var service = Open(path)) { await service.EnsureAllCollectionsAsync(); await Activate(service, Media("original")); }
                if (failure == "missing") File.Delete(Path.Combine(path, "active.json"));
                if (failure == "schema")
                {
                    var manifest = Path.Combine(path, "active.json");
                    File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Schema\":1", "\"Schema\":0"));
                }
                if (failure == "corrupt")
                {
                    var index = Directory.GetDirectories(path, "g-*").Single();
                    var segments = Directory.GetFiles(index, "segments_*").Single();
                    File.WriteAllBytes(segments, [0, 1, 2, 3]);
                }
                using var recovered = Open(path);
                (await recovered.EnsureAllCollectionsAsync()).ShouldBeTrue(failure);
                await Activate(recovered, Media("rebuilt"));
                (await recovered.SearchAsync<MediaDocument>(SearchCollection.Media, new("rebuilt", "title"))).Found.ShouldBe(1);
            }
            finally { Directory.Delete(path, true); }
        }
    }
    [Test]
    public async Task Activation_Is_Atomic_For_Readers_Failed_Publication_Retains_Active_And_Generations_Are_Bounded()
    {
        var path = Path.Combine(Path.GetTempPath(), "lucene-activate-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = Open(path); await service.EnsureAllCollectionsAsync();
            await Activate(service, Media("old"), Media("old"));
            // A failed manifest write models filesystem publication failures without changing global mounts.
            Directory.CreateDirectory(Path.Combine(path, "active.json.tmp"));
            await Should.ThrowAsync<UnauthorizedAccessException>(() => Activate(service, Media("new")));
            (await service.SearchAsync<MediaDocument>(SearchCollection.Media, new("old", "title"))).Found.ShouldBe(2);
            Directory.Delete(Path.Combine(path, "active.json.tmp"));
            using var stop = new CancellationTokenSource();
            var reading = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var result = await service.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title"));
                    (result.Found is 1 or 2).ShouldBeTrue();
                }
            });
            try
            {
                for (var i = 0; i < 5; i++) await Activate(service, Media("new"));
            }
            finally { stop.Cancel(); await reading; }
            Directory.GetDirectories(path, "g-*").Length.ShouldBe(2);
            using var secondOwner = Open(path);
            await Should.ThrowAsync<IOException>(() => secondOwner.EnsureAllCollectionsAsync());
        }
        finally { Directory.Delete(path, true); }
    }
    [Test]
    public async Task Committed_Index_Survives_Process_Kill_Without_Disposal()
    {
        var path = Path.Combine(Path.GetTempPath(), "lucene-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(LuceneLifecycleTests).Assembly.Location);
            start.ArgumentList.Add("--treenode-filter"); start.ArgumentList.Add("/*/*/LuceneLifecycleTests/CommitProcessProbe");
            start.Environment["FROSTSTREAM_LUCENE_CRASH_PATH"] = path;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(Path.Combine(path, "committed")))
                {
                    if (process.HasExited) throw new InvalidOperationException(await output + await errors);
                    await Task.Delay(20, timeout.Token);
                }
                process.Kill(true); await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                await Task.WhenAll(output, errors);
            }
            using var recovered = Open(path);
            (await recovered.EnsureAllCollectionsAsync()).ShouldBeFalse();
            (await recovered.SearchAsync<MediaDocument>(SearchCollection.Media, new("committed", "title"))).Found.ShouldBe(1);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Test]
    public async Task Disk_Full_During_Replacement_Retains_Durable_Active_Search()
    {
        var path = Environment.GetEnvironmentVariable("FROSTSTREAM_LUCENE_DISK_FULL_PATH"); if (path is null) return;
        using var service = Open(path); await service.EnsureAllCollectionsAsync(); await Activate(service, Media("retained"));
        // Run in a separate test container with a 1 MiB tmpfs mounted at this directory.
        var random = new byte[2 * 1024 * 1024]; new Random(42).NextBytes(random);
        await Should.ThrowAsync<IOException>(async () =>
        {
            await using var replacement = await service.CreateGenerationAsync();
            await replacement.Writer.BulkImportCaptionsAsync([new CaptionDocument
            {
                Id = "large", MediaGuid = "media", LanguageCode = "en", CaptionType = "manual", StoragePath = "large.vtt",
                Text = Convert.ToBase64String(random)
            }]);
            await replacement.ActivateAsync();
        });
        (await service.SearchAsync<MediaDocument>(SearchCollection.Media, new("retained", "title"))).Found.ShouldBe(1);
    }

    [Test]
    public async Task CommitProcessProbe()
    {
        var path = Environment.GetEnvironmentVariable("FROSTSTREAM_LUCENE_CRASH_PATH"); if (path is null) return;
        using var service = Open(path); await service.EnsureAllCollectionsAsync(); await Activate(service);
        await service.UpsertMediaAsync(Media("committed"));
        await File.WriteAllTextAsync(Path.Combine(path, "committed"), "durable");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
