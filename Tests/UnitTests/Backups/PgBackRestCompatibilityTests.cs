using BackupService;
using BackupService.PgBackRest;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Backups;

public sealed class PgBackRestCompatibilityTests
{
    [Test]
    public async Task Runtime_Validation_Accepts_An_Existing_Stanza_Without_Mutating_It()
    {
        var runner = new StubRunner(new PgBackRestStanzaInfo
        {
            Name = "froststream",
            Status = new PgBackRestStatus { Code = 2, Message = "no valid backups" }
        });

        await runner.ValidateStanzaAsync(CancellationToken.None);

        runner.InfoCalls.ShouldBe(1);
    }

    [Test]
    public async Task Runtime_Validation_Rejects_A_Missing_Stanza_With_Init_Instruction()
    {
        var runner = new StubRunner(new PgBackRestStanzaInfo
        {
            Name = "froststream",
            Status = new PgBackRestStatus { Code = 1, Message = "missing stanza path" }
        });

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => runner.ValidateStanzaAsync(CancellationToken.None));

        exception.Message.ShouldContain("init profile 'frostream-full-init'");
    }

    private sealed class StubRunner(PgBackRestStanzaInfo? info) : PgBackRestRunner(
        new BackupServiceOptions { Directory = "/tmp", PgDataPath = "/tmp", Stanza = "froststream" },
        NullLogger<PgBackRestRunner>.Instance)
    {
        public int InfoCalls { get; private set; }

        public override Task<PgBackRestStanzaInfo?> InfoAsync(CancellationToken cancellationToken)
        {
            InfoCalls++;
            return Task.FromResult(info);
        }
    }
}
