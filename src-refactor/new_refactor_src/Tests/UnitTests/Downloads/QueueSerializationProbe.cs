using System.Text.Json;
using Conduit.NATS;
using NodaTime;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Downloads;

// TEMPORARY diagnostic: exercises the exact Conduit.NATS JSON options used for NATS request/reply,
// to reproduce the live queue-list 502 (which the mocked-bus tests never hit).
public sealed class QueueSerializationProbe
{
    private static readonly JsonSerializerOptions Options = JsonSerializerRegistry.CreateDefaultOptions();

    [Test]
    public void Request_RoundTrips()
    {
        var req = new DownloadQueueListRequest { Limit = 50 };
        var json = JsonSerializer.Serialize(req, Options);
        TestContext.Current?.OutputWriter.WriteLine($"REQ JSON: {json}");
        var back = JsonSerializer.Deserialize<DownloadQueueListRequest>(json, Options);
        back.ShouldNotBeNull();
        back.Limit.ShouldBe(50);
    }

    [Test]
    public void Request_With_Filters_RoundTrips()
    {
        var req = new DownloadQueueListRequest
        {
            State = DownloadJobState.DownloadQueued,
            SourceKind = DownloadSourceKind.Playlist,
            CreatedFrom = Instant.FromUtc(2026, 1, 1, 0, 0),
            Sort = DownloadQueueSort.Priority,
            Query = "cats"
        };
        var json = JsonSerializer.Serialize(req, Options);
        TestContext.Current?.OutputWriter.WriteLine($"REQ2 JSON: {json}");
        JsonSerializer.Deserialize<DownloadQueueListRequest>(json, Options).ShouldNotBeNull();
    }

    [Test]
    public void Empty_Response_RoundTrips()
    {
        var resp = new DownloadQueueListResponse { Success = true, Items = [], TotalCount = 0 };
        var json = JsonSerializer.Serialize(resp, Options);
        TestContext.Current?.OutputWriter.WriteLine($"RESP JSON: {json}");
        JsonSerializer.Deserialize<DownloadQueueListResponse>(json, Options).ShouldNotBeNull();
    }

    [Test]
    public void Populated_Response_RoundTrips()
    {
        var resp = new DownloadQueueListResponse
        {
            Success = true,
            TotalCount = 1,
            Items =
            [
                new DownloadQueueJobDto
                {
                    JobId = Guid.NewGuid(),
                    SourceUrl = "https://example.test/v",
                    State = DownloadJobState.Completed,
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
                }
            ]
        };
        var json = JsonSerializer.Serialize(resp, Options);
        TestContext.Current?.OutputWriter.WriteLine($"RESP2 JSON: {json}");
        JsonSerializer.Deserialize<DownloadQueueListResponse>(json, Options)!.Items.Count.ShouldBe(1);
    }
}
