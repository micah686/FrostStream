using BackupService;
using BackupService.PgBackRest;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Backups;

/// <summary>
/// Exercises OpenBaoPairing's file-level behavior (checksums, presence, pruning) by seeding export
/// files directly. The export/restore calls themselves reach a live OpenBao over HTTP (same as
/// OpenBaoBackup) and aren't covered here.
/// </summary>
public sealed class OpenBaoPairingTests
{
    [Test]
    public async Task HasExport_And_HasSnapshotExport_Reflect_Independent_Files()
    {
        var root = NewRoot();
        try
        {
            var openBaoDir = Path.Combine(root, "openbao");
            Directory.CreateDirectory(openBaoDir);
            await File.WriteAllTextAsync(Path.Combine(openBaoDir, "label-1.json"), "{}");
            var pairing = NewPairing(root);

            pairing.HasExport("label-1").ShouldBeTrue();
            pairing.HasSnapshotExport("label-1").ShouldBeFalse();
        }
        finally
        {
            CleanUp(root);
        }
    }

    [Test]
    public async Task RestoreAsync_Throws_When_No_Export_Exists()
    {
        var root = NewRoot();
        try
        {
            var pairing = NewPairing(root);
            await Should.ThrowAsync<FileNotFoundException>(
                () => pairing.RestoreAsync("never-backed-up", CancellationToken.None));
        }
        finally
        {
            CleanUp(root);
        }
    }

    [Test]
    public async Task RestoreSnapshotAsync_Throws_When_No_Snapshot_Exists()
    {
        var root = NewRoot();
        try
        {
            var pairing = NewPairing(root);
            await Should.ThrowAsync<FileNotFoundException>(
                () => pairing.RestoreSnapshotAsync("never-backed-up", CancellationToken.None));
        }
        finally
        {
            CleanUp(root);
        }
    }

    [Test]
    public async Task RestoreSnapshotAsync_Rejects_Tampered_Snapshot_Before_Streaming_It()
    {
        var root = NewRoot();
        try
        {
            var openBaoDir = Path.Combine(root, "openbao");
            Directory.CreateDirectory(openBaoDir);
            var snapshotFile = Path.Combine(openBaoDir, "label-1.raft-snapshot");
            await File.WriteAllBytesAsync(snapshotFile, [1, 2, 3]);
            await File.WriteAllTextAsync(
                snapshotFile + ".sha256",
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([1, 2, 3])));
            // Corrupt the file after the checksum was written.
            await File.WriteAllBytesAsync(snapshotFile, [9, 9, 9]);
            var pairing = NewPairing(root);

            // No OpenBaoAddress is reachable in this test; a checksum failure must be caught
            // before any HTTP call is attempted, so this must throw on the checksum, not a
            // connection error.
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => pairing.RestoreSnapshotAsync("label-1", CancellationToken.None));
            ex.Message.ShouldContain("checksum");
        }
        finally
        {
            CleanUp(root);
        }
    }

    [Test]
    public void PruneExcept_Deletes_Kv_Export_Snapshot_And_Sidecars_For_Expired_Labels()
    {
        var root = NewRoot();
        try
        {
            var openBaoDir = Path.Combine(root, "openbao");
            Directory.CreateDirectory(openBaoDir);
            SeedPair(openBaoDir, "expired-label");
            SeedPair(openBaoDir, "live-label");
            var pairing = NewPairing(root);

            pairing.PruneExcept(["live-label"]);

            Directory.EnumerateFiles(openBaoDir, "expired-label*").ShouldBeEmpty();
            File.Exists(Path.Combine(openBaoDir, "live-label.json")).ShouldBeTrue();
            File.Exists(Path.Combine(openBaoDir, "live-label.raft-snapshot")).ShouldBeTrue();
        }
        finally
        {
            CleanUp(root);
        }
    }

    [Test]
    public void PruneExcept_Prunes_A_Snapshot_Only_Label_Even_Without_A_Matching_Json_File()
    {
        // Regression coverage: an earlier version of PruneExcept only discovered candidate labels
        // from *.json files, so a label with only a snapshot (or only a bootstrap-style companion
        // file) would never be pruned. Labels must be derived from either file type.
        var root = NewRoot();
        try
        {
            var openBaoDir = Path.Combine(root, "openbao");
            Directory.CreateDirectory(openBaoDir);
            WriteSnapshotOnly(openBaoDir, "snapshot-only-label");
            var pairing = NewPairing(root);

            pairing.PruneExcept(["some-other-live-label"]);

            File.Exists(Path.Combine(openBaoDir, "snapshot-only-label.raft-snapshot")).ShouldBeFalse();
        }
        finally
        {
            CleanUp(root);
        }
    }

    [Test]
    public void PruneExcept_NoOps_When_Export_Directory_Does_Not_Exist()
    {
        var root = NewRoot();
        try
        {
            var pairing = NewPairing(root);
            Should.NotThrow(() => pairing.PruneExcept([]));
        }
        finally
        {
            CleanUp(root);
        }
    }

    private static void SeedPair(string openBaoDir, string label)
    {
        File.WriteAllText(Path.Combine(openBaoDir, $"{label}.json"), "{}");
        File.WriteAllText(Path.Combine(openBaoDir, $"{label}.json.sha256"), "deadbeef");
        File.WriteAllBytes(Path.Combine(openBaoDir, $"{label}.raft-snapshot"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(openBaoDir, $"{label}.raft-snapshot.sha256"), "deadbeef");
    }

    private static void WriteSnapshotOnly(string openBaoDir, string label)
        => File.WriteAllBytes(Path.Combine(openBaoDir, $"{label}.raft-snapshot"), [1, 2, 3]);

    private static OpenBaoPairing NewPairing(string root)
        => new(new BackupServiceOptions { Directory = root }, NullLogger<OpenBaoPairing>.Instance);

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), $"froststream-openbao-pairing-tests-{Guid.NewGuid():N}");

    private static void CleanUp(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
