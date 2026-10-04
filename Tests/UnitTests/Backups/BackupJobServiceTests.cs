using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shared.Backups;
using Shared.Deployment;
using Shouldly;
using WebAPI.Features.Backups;

namespace UnitTests.Backups;

public sealed class BackupJobServiceTests
{
    [Test]
    public async Task Full_Retains_Differential_And_Deep_Verification_And_Rejects_Unknown_Types_Before_Dispatch()
    {
        var client = Substitute.For<IBackupServiceClient>();
        var capabilities = SystemCapabilities.Resolve(new(DeploymentMode.Full), new ConfigurationBuilder().Build());
        var service = new BackupJobService(client, capabilities);
        var job = new BackupJobDto(Guid.NewGuid(), "backup", "diff", "queued", "test", null, null,
            DateTimeOffset.UtcNow, null, []);
        client.CreateAsync(Arg.Any<CreateBackupJobRequest>(), Arg.Any<CancellationToken>()).Returns(job);
        client.VerifyAsync(Arg.Any<VerifyBackupRequest>(), Arg.Any<CancellationToken>()).Returns(job);

        (await service.StartBackupAsync("test", " DIFF ", CancellationToken.None)).JobId.ShouldBe(job.JobId);
        await client.Received(1).CreateAsync(Arg.Is<CreateBackupJobRequest>(request => request.Type == "diff"), CancellationToken.None);
        await service.VerifyAsync("label", true, CancellationToken.None);
        await client.Received(1).VerifyAsync(new VerifyBackupRequest("label", true), CancellationToken.None);
        await Should.ThrowAsync<NotSupportedException>(() => service.StartBackupAsync(null, "pitr", CancellationToken.None));
        await client.Received(1).CreateAsync(Arg.Any<CreateBackupJobRequest>(), Arg.Any<CancellationToken>());
    }
}
