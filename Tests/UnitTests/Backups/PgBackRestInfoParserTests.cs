using BackupService.PgBackRest;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Backups;

public sealed class PgBackRestInfoParserTests
{
    private const string HealthyInfoJson = """
        [
          {
            "archive": [
              {
                "database": { "id": 1, "repo-key": 1 },
                "id": "18-1",
                "max": "0000000100000000000000A5",
                "min": "000000010000000000000001"
              }
            ],
            "backup": [
              {
                "annotation": { "name": "pre-upgrade" },
                "archive": { "start": "000000010000000000000004", "stop": "000000010000000000000004" },
                "backrest": { "format": 5, "version": "2.59.0" },
                "database": { "id": 1, "repo-key": 1 },
                "error": false,
                "info": {
                  "delta": 44712004,
                  "repository": { "delta": 5544396, "size": 5544396 },
                  "size": 44712004
                },
                "label": "20260801-030000F",
                "lsn": { "start": "0/4000028", "stop": "0/4000138" },
                "prior": null,
                "reference": null,
                "timestamp": { "start": 1785466800, "stop": 1785466815 },
                "type": "full"
              },
              {
                "annotation": { "name": "nightly" },
                "archive": { "start": "000000010000000000000009", "stop": "000000010000000000000009" },
                "backrest": { "format": 5, "version": "2.59.0" },
                "database": { "id": 1, "repo-key": 1 },
                "error": false,
                "info": {
                  "delta": 1024000,
                  "repository": { "delta": 210044, "size": 5754440 },
                  "size": 44713004
                },
                "label": "20260801-030000F_20260802-020000D",
                "lsn": { "start": "0/9000028", "stop": "0/9000138" },
                "prior": "20260801-030000F",
                "reference": [ "20260801-030000F" ],
                "timestamp": { "start": 1785549600, "stop": 1785549605 },
                "type": "diff"
              }
            ],
            "cipher": "none",
            "db": [ { "id": 1, "repo-key": 1, "system-id": 7300000000000000000, "version": "18" } ],
            "name": "froststream",
            "repo": [ { "cipher": "none", "key": 1, "status": { "code": 0, "message": "ok" } } ],
            "status": { "code": 0, "lock": { "backup": { "held": false } }, "message": "ok" }
          }
        ]
        """;

    [Test]
    public async Task Parses_Backups_With_Annotations_Sizes_And_Wal_Ranges()
    {
        var stanza = PgBackRestInfoParser.Parse(HealthyInfoJson, "froststream").ShouldNotBeNull();

        stanza.Status.ShouldNotBeNull().Code.ShouldBe(0);
        stanza.Backup.Count.ShouldBe(2);

        var full = stanza.Backup[0];
        full.Label.ShouldBe("20260801-030000F");
        full.Type.ShouldBe("full");
        full.AnnotatedName.ShouldBe("pre-upgrade");
        full.Prior.ShouldBeNull();
        full.Error.ShouldBe(false);
        full.Info.ShouldNotBeNull().Size.ShouldBe(44712004);
        full.Info!.Repository.ShouldNotBeNull().Delta.ShouldBe(5544396);
        full.WalRange.ShouldNotBeNull().Start.ShouldBe("000000010000000000000004");
        full.StartedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1785466800));
        full.CompletedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1785466815));

        var diff = stanza.Backup[1];
        diff.Type.ShouldBe("diff");
        diff.Prior.ShouldBe("20260801-030000F");

        stanza.Archive.Count.ShouldBe(1);
        stanza.Archive[0].Min.ShouldBe("000000010000000000000001");
        stanza.Archive[0].Max.ShouldBe("0000000100000000000000A5");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Missing_Stanza_Returns_Null()
    {
        PgBackRestInfoParser.Parse("[]", "froststream").ShouldBeNull();
        PgBackRestInfoParser.Parse(HealthyInfoJson, "other-stanza").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Uninitialized_Stanza_Reports_Nonzero_Status_And_No_Backups()
    {
        const string missing = """
            [
              {
                "backup": [],
                "db": [],
                "name": "froststream",
                "repo": [ { "cipher": "none", "key": 1, "status": { "code": 1, "message": "missing stanza path" } } ],
                "status": { "code": 1, "lock": { "backup": { "held": false } }, "message": "missing stanza path" }
              }
            ]
            """;

        var stanza = PgBackRestInfoParser.Parse(missing, "froststream").ShouldNotBeNull();
        stanza.Status.ShouldNotBeNull().Code.ShouldBe(1);
        stanza.Backup.ShouldBeEmpty();
        stanza.Archive.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Unknown_Fields_Are_Ignored()
    {
        const string withExtras = """
            [ { "name": "froststream", "status": { "code": 0, "some-future-field": true }, "brand-new-section": [1, 2, 3] } ]
            """;

        var stanza = PgBackRestInfoParser.Parse(withExtras, "froststream").ShouldNotBeNull();
        stanza.Status.ShouldNotBeNull().Code.ShouldBe(0);
        await Task.CompletedTask;
    }
}
