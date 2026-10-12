using DataBridge;
using DataBridge.Messaging;
using DataBridge.Persistence.Indexing;
using DataBridge.Search;
using FrostStream.Integrations.Typesense;
using FrostStream.Lite.Infrastructure.Search;
using FrostStream.Lite.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

/// <summary>Always exercises embedded Lite; opt into Full with an owned disposable Typesense instance.</summary>
[NotInParallel("Phase4Typesense")]
public sealed class SearchProviderContractTests
{
    [Test]
    public async Task Both_Editions_Use_Typed_Search_And_Preserve_Active_Generations()
    {
        var url = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_TYPESENSE");
        foreach (var edition in new[] { "Lite", "Full" })
        {
            if (edition == "Full" && string.IsNullOrWhiteSpace(url)) continue;
            var directory = Path.Combine(Path.GetTempPath(), "phase5-contract-" + Guid.NewGuid().ToString("N"));
            try
            {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration["Deployment:Mode"] = edition;
            builder.Configuration["Typesense:Url"] = url;
            builder.Configuration["Typesense:ApiKey"] = "phase4-test-key";
            if (edition == "Full") builder.AddTypesenseIntegration();
            else
            {
                builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
                builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "test.db");
                builder.Configuration["Search:Lucene:Path"] = Path.Combine(directory, "search");
                builder.AddLuceneSearch();
            }
            using var host = builder.Build();
            var writer = host.Services.GetRequiredService<ISearchIndexWriter>();
            var reader = host.Services.GetRequiredService<ISearchReader>();
            await writer.EnsureAllCollectionsAsync();
            var first = Media("Café 日本語 graphics", 600, 100);
            var second = Media("Other graphics", 100, 200);
            await using (var initial = await writer.CreateGenerationAsync())
            {
                await initial.Writer.BulkImportMediaAsync([first, second]);
                await initial.Writer.BulkImportCommentsAsync([new CommentDocument
                {
                    Id = "comment", MediaGuid = first.Id, ParentCommentId = "", Text = "commentphrase",
                    Platform = "youtube", AccountName = "Author", AccountHandle = "author"
                }]);
                await initial.Writer.BulkImportCaptionsAsync([new CaptionDocument
                {
                    Id = "caption", MediaGuid = first.Id, LanguageCode = "ja", CaptionType = "manual",
                    Name = "日本語", StoragePath = "captions.vtt", Text = "captionphrase"
                }]);
                await initial.ActivateAsync();
            }
            await VerifyHandlers(reader, first, second);
            var parsed = AdvancedQueryParser.Parse("channel:\"Test Channel\" codec:h265 duration:>=600 Café");
            var filtered = await reader.SearchAsync<MediaDocument>(SearchCollection.Media,
                new SearchRequest(parsed.EffectiveQuery, "title") { Filter = parsed.Filter });
            filtered.Found.ShouldBe(1); filtered.Hits.Single().Document.Id.ShouldBe(first.Id);
            foreach (var sort in new[] { "added_at_sort", "release_date_sort", "view_count", "like_count", "duration_seconds", "video_height", "title" })
            {
                var page = await reader.SearchAsync<MediaDocument>(SearchCollection.Media,
                    new SearchRequest("*", "title") { PerPage = 1, Page = 2, SortBy = new(sort, true) });
                page.Found.ShouldBe(2); page.Hits.Count.ShouldBe(1);
            }
            var comments = await reader.SearchAsync<CommentDocument>(SearchCollection.Comments,
                new SearchRequest("commentphrase", "text") { Filter = new SearchFilter.Text("media_guid", first.Id) });
            comments.Found.ShouldBe(1);
            var captions = await reader.SearchAsync<CaptionDocument>(SearchCollection.Captions,
                new SearchRequest("captionphrase", "text") { Filter = new SearchFilter.Text("language_code", "ja") });
            captions.Found.ShouldBe(1);
            await using (var failed = await writer.CreateGenerationAsync())
            {
                // A partially built generation fails to import a document. Disposal must retain active search.
                await Should.ThrowAsync<InvalidOperationException>(() => failed.Writer.BulkImportMediaAsync([first with { Title = null! }]));
            }
            (await reader.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title"))).Found.ShouldBe(2);
            if (edition == "Full")
            {
            // A fresh router simulates restart and resolves all collections from the single alias.
            var router = new TypesenseGenerationRouter(host.Services.GetRequiredService<Typesense.ITypesenseClient>());
            (await router.LoadAsync(default)).ShouldBeTrue();
            var restarted = new TypesenseSearchReader(host.Services.GetRequiredService<Typesense.ITypesenseClient>(), router);
            (await restarted.SearchAsync<CaptionDocument>(SearchCollection.Captions, new("captionphrase", "text"))).Found.ShouldBe(1);
            }
            await writer.DeleteMediaByGuidAsync(first.Id);
            await writer.DeleteCommentsByMediaGuidAsync(first.Id);
            await writer.DeleteCaptionsByMediaGuidAsync(first.Id);
            (await reader.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title"))).Found.ShouldBe(1);
            (await reader.SearchAsync<CommentDocument>(SearchCollection.Comments, new("*", "text"))).Found.ShouldBe(0);
            (await reader.SearchAsync<CaptionDocument>(SearchCollection.Captions, new("*", "text"))).Found.ShouldBe(0);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }

    [Test]
    public async Task Failed_Or_Canceled_Rebuild_Retains_Search_And_Reports_Degraded_Status()
    {
        var url = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_TYPESENSE");
        var directory = Path.Combine(Path.GetTempPath(), "phase4-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
            builder.Configuration["Deployment:Mode"] = "Lite";
            builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
            builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "test.db");
            builder.Configuration["Typesense:Url"] = url;
            builder.Configuration["Typesense:ApiKey"] = "phase4-test-key";
            builder.AddLitePersistence(); builder.AddLuceneSearch();
            var source = Substitute.For<IMediaDocumentQuery>();
            builder.Services.AddSingleton(source);
            using var host = builder.Build(); host.InitializeDataBridge();
            var writer = host.Services.GetRequiredService<ISearchIndexWriter>();
            await writer.EnsureAllCollectionsAsync();
            var media = Media("Retained", 10, 10);
            await using (var initial = await writer.CreateGenerationAsync())
            {
                await initial.Writer.UpsertMediaAsync(media); await initial.ActivateAsync();
            }
            var status = new SearchStatus(); status.Set(SearchState.Ready);
            var coordinator = new MetadataRebuildCoordinator(host.Services.GetRequiredService<IServiceScopeFactory>(),
                new SearchWriterGate(), host.Services.GetRequiredService<PendingIndexStore>(), writer, status,
                new CaptionDocumentHydrator(Substitute.For<Shared.Storage.IStoreProvider>(), NullLogger<CaptionDocumentHydrator>.Instance),
                Substitute.For<IHostApplicationLifetime>(), Substitute.For<INotificationDispatcher>(), NullLogger<MetadataRebuildCoordinator>.Instance, host.Services.GetRequiredService<ISearchActivationBarrier>());
            source.GetMediaBatchAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DocumentBatch<MediaDocument>>(new IOException("source failed")));
            await Should.ThrowAsync<IOException>(() => coordinator.RebuildAsync("test failure"));
            status.Current.State.ShouldBe(SearchState.Degraded);
            var reader = host.Services.GetRequiredService<ISearchReader>();
            (await reader.SearchAsync<MediaDocument>(SearchCollection.Media, new("Retained", "title"))).Found.ShouldBe(1);
            source.GetMediaBatchAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DocumentBatch<MediaDocument>>(new OperationCanceledException()));
            using var cancel = new CancellationTokenSource();
            source.GetMediaBatchAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => { cancel.Cancel(); return Task.FromCanceled<DocumentBatch<MediaDocument>>(cancel.Token); });
            await Should.ThrowAsync<OperationCanceledException>(() => coordinator.RebuildAsync("cancel", cancel.Token));
            (await reader.SearchAsync<MediaDocument>(SearchCollection.Media, new("Retained", "title"))).Found.ShouldBe(1);
            // Work committed during a rebuild is replayed before activation and remains durable
            // until the normal revision worker acknowledges the active generation.
            source.GetMediaBatchAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new DocumentBatch<MediaDocument>([], 0));
            source.GetCommentBatchAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new DocumentBatch<CommentDocument>([], 0));
            source.GetCaptionBatchAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new DocumentBatch<CaptionDocument>([], 0));
            source.GetCommentsForMediaBatchAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new DocumentBatch<CommentDocument>([], 0));
            source.GetCaptionsForMediaBatchAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new DocumentBatch<CaptionDocument>([], 0));
            source.GetMediaByGuidAsync(Guid.ParseExact(media.Id, "N"), Arg.Any<CancellationToken>()).Returns(media with { Title = "Updated" });
            var pending = host.Services.GetRequiredService<PendingIndexStore>();
            await pending.RequestAsync(Guid.ParseExact(media.Id, "N"));
            (await coordinator.RebuildAsync("catch up committed mutation")).Accepted.ShouldBeTrue();
            status.Current.State.ShouldBe(SearchState.Ready);
            (await reader.SearchAsync<MediaDocument>(SearchCollection.Media, new("Updated", "title"))).Found.ShouldBe(1);
            var work = (await pending.ClaimAsync(default)).ShouldNotBeNull();
            await pending.ReleaseAsync(work, default);
            source.GetMediaByGuidAsync(Guid.ParseExact(media.Id, "N"), Arg.Any<CancellationToken>()).Returns((MediaDocument?)null);
            await pending.RequestAsync(Guid.ParseExact(media.Id, "N"));
            await coordinator.RebuildAsync("catch up deletion");
            (await reader.SearchAsync<MediaDocument>(SearchCollection.Media, new("*", "title"))).Found.ShouldBe(0);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task VerifyHandlers(ISearchReader reader, MediaDocument first, MediaDocument second)
    {
        var source = Substitute.For<IMediaDocumentQuery>();
        source.GetMediaByGuidAsync(Guid.ParseExact(first.Id, "N"), Arg.Any<CancellationToken>()).Returns(first);
        source.GetMediaByGuidsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { first, second }.Where(x => call.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(Guid.ParseExact(x.Id, "N"))).ToArray());
        source.GetCommentsByMediaGuidAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new[]
        {
            new CommentDocument { Id = "comment", MediaGuid = first.Id, ParentCommentId = "", Text = "commentphrase", Platform = "youtube", AccountName = "Author", AccountHandle = "author" }
        });
        var notes = Substitute.For<global::DataBridge.Data.IUserNotesRepository>();
        notes.SearchVideoNoteTargetsAsync(Arg.Any<string>(), "privatenote", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0) == "alice" ? new[] { Guid.ParseExact(first.Id, "N") } : []);
        notes.GetVideoNotesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        notes.GetChannelNotesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());
        using var services = new ServiceCollection().AddSingleton(source).AddSingleton(notes).BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        await using var bus = new Shared.Messaging.Adapters.LocalApplicationTransport(NullLogger<Shared.Messaging.Adapters.LocalApplicationTransport>.Instance);
        var status = new SearchStatus(); status.Set(SearchState.Ready);
        using var unified = new UnifiedSearchConsumerService(bus, reader, status, scopes, NullLogger<UnifiedSearchConsumerService>.Instance);
        using var library = new MetadataListConsumerService(bus, reader, scopes, NullLogger<MetadataListConsumerService>.Instance);
        using var comments = new MetadataCommentsConsumerService(bus, scopes, NullLogger<MetadataCommentsConsumerService>.Instance);
        using var captions = new MetadataCaptionsConsumerService(bus, reader, NullLogger<MetadataCaptionsConsumerService>.Instance);
        foreach (var handler in new Shared.Messaging.SubscriptionBackgroundService[] { unified, library, comments, captions })
        {
            await handler.StartAsync(default); await handler.RegistrationCompleted;
        }
        try
        {
            var timeout = TimeSpan.FromSeconds(5);
            var list = await bus.RequestAsync<Shared.Messaging.MetadataListRequestMessage, Shared.Messaging.MetadataListResponseMessage>(
                Shared.Messaging.MetadataSubjects.List, new() { Platform = "youtube", PageSize = 1 }, timeout);
            list.ShouldNotBeNull().Success.ShouldBeTrue(); list.TotalCount.ShouldBe(2); list.HasMore.ShouldBeTrue();
            foreach (var (query, scope, reason) in new[] { ("graphics", "metadata", "metadata"), ("commentphrase", "comments", "comments"), ("captionphrase", "subtitles", "subtitles") })
            {
                var result = await bus.RequestAsync<Shared.Messaging.SearchQueryRequestMessage, Shared.Messaging.SearchQueryResponseMessage>(
                    Shared.Messaging.SearchSubjects.Query, new() { Query = query, Scope = scope }, timeout);
                result.ShouldNotBeNull().Success.ShouldBeTrue(); result.Items.ShouldNotBeEmpty();
                result.Items.All(x => x.MatchedIn.Contains(reason)).ShouldBeTrue();
                result.Items.Select(x => x.Media.MediaGuid).Distinct().Count().ShouldBe(result.Items.Count);
            }
            foreach (var owner in new[] { "alice", "bob" })
            {
                var result = await bus.RequestAsync<Shared.Messaging.SearchQueryRequestMessage, Shared.Messaging.SearchQueryResponseMessage>(
                    Shared.Messaging.SearchSubjects.Query, new() { Query = "privatenote", OwnerSubject = owner }, timeout);
                result.ShouldNotBeNull().Success.ShouldBeTrue(); result.Items.Count.ShouldBe(owner == "alice" ? 1 : 0);
            }
            var filteredNote = await bus.RequestAsync<Shared.Messaging.SearchQueryRequestMessage, Shared.Messaging.SearchQueryResponseMessage>(
                Shared.Messaging.SearchSubjects.Query, new() { Query = "platform:vimeo privatenote", OwnerSubject = "alice" }, timeout);
            filteredNote.ShouldNotBeNull().Items.ShouldBeEmpty();
            var similar = await bus.RequestAsync<Shared.Messaging.SearchSimilarRequestMessage, Shared.Messaging.SearchSimilarResponseMessage>(
                Shared.Messaging.SearchSubjects.Similar, new() { MediaGuid = Guid.ParseExact(first.Id, "N") }, timeout);
            similar.ShouldNotBeNull().Success.ShouldBeTrue(); similar.Items.Select(x => x.Media.MediaGuid).ShouldNotContain(Guid.ParseExact(first.Id, "N"));
            var tracks = await bus.RequestAsync<Shared.Messaging.MetadataCaptionsListRequestMessage, Shared.Messaging.MetadataCaptionsListResponseMessage>(
                Shared.Messaging.MetadataSubjects.CaptionsList, new() { MediaGuid = Guid.ParseExact(first.Id, "N"), LanguageCode = "ja" }, timeout);
            tracks.ShouldNotBeNull().Success.ShouldBeTrue(); tracks.Items.Count.ShouldBe(1);
            var commentList = await bus.RequestAsync<Shared.Messaging.MetadataCommentsListRequestMessage, Shared.Messaging.MetadataCommentsListResponseMessage>(
                Shared.Messaging.MetadataSubjects.CommentsList, new() { MediaGuid = Guid.ParseExact(first.Id, "N"), Query = "commentphrase" }, timeout);
            commentList.ShouldNotBeNull().Success.ShouldBeTrue(); commentList.TotalCount.ShouldBe(1);
            var health = await bus.RequestAsync<SearchStatusRequest, SearchStatusSnapshot>(Shared.Messaging.SearchSubjects.Status, new(), timeout);
            health.ShouldNotBeNull().State.ShouldBe(SearchState.Ready);
        }
        finally
        {
            foreach (var handler in new Shared.Messaging.SubscriptionBackgroundService[] { unified, library, comments, captions }) await handler.StopAsync(default);
        }
    }

    private static MediaDocument Media(string title, int duration, int views) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Title = title, Platform = "youtube", AccountName = "Test Channel",
        AccountHandle = "test", AccountId = 1, VideoCodec = "hevc", DurationSeconds = duration,
        ViewCount = views, LikeCount = views, VideoHeight = 1080, AddedAtSort = views, ReleaseDateSort = views
    };
}
