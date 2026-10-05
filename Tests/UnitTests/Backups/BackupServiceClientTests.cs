using System.Net;
using System.Net.Http.Json;
using Shared.Backups;
using Shouldly;

namespace UnitTests.Backups;

public sealed class BackupServiceClientTests
{
    [Test]
    public async Task Full_Http_Adapter_Preserves_Internal_Routes_Payloads_And_NotFound()
    {
        var id = Guid.NewGuid();
        var job = new BackupJobDto(id, "backup", "diff", "queued", "release", null, null,
            DateTimeOffset.UtcNow, null, []);
        var repository = new BackupRepositoryDto(true, null, [], new(null, null));
        using var handler = new RecordingHandler(job, repository);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://backupservice:8080") };
        var client = new BackupServiceClient(http);
        (await client.CreateAsync(new("release", "diff"))).JobId.ShouldBe(id);
        handler.LastMethod.ShouldBe(HttpMethod.Post);
        handler.LastPath.ShouldBe("/internal/backups/jobs");
        handler.LastBody.ShouldContain("\"type\":\"diff\"");
        (await client.VerifyAsync(new("label", Deep: true))).JobId.ShouldBe(id);
        handler.LastPath.ShouldBe("/internal/backups/verify");
        handler.LastBody.ShouldContain("\"deep\":true");
        (await client.ListJobsAsync()).ShouldHaveSingleItem().JobId.ShouldBe(id);
        handler.LastMethod.ShouldBe(HttpMethod.Get);
        handler.LastPath.ShouldBe("/internal/backups/jobs");
        (await client.ListBackupsAsync()).RepositoryOk.ShouldBeTrue();
        handler.LastPath.ShouldBe("/internal/backups/backups");
        (await client.GetJobAsync(id))!.JobId.ShouldBe(id);
        handler.LastPath.ShouldBe($"/internal/backups/jobs/{id}");
        handler.Status = HttpStatusCode.NotFound;
        (await client.GetJobAsync(Guid.NewGuid())).ShouldBeNull();
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Should.ThrowAsync<HttpRequestException>(() => client.ListBackupsAsync());
    }

    private sealed class RecordingHandler(BackupJobDto job, BackupRepositoryDto repository) : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string LastBody { get; private set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            object body = LastPath == "/internal/backups/backups" ? repository
                : LastPath == "/internal/backups/jobs" && request.Method == HttpMethod.Get ? new[] { job } : job;
            return new(Status) { Content = JsonContent.Create(body) };
        }
    }
}
