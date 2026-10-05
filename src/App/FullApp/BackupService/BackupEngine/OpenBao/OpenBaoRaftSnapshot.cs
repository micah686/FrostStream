namespace BackupService;

/// <summary>
/// Saves and restores OpenBao's actual storage (the <c>openbao-data</c> volume, an integrated
/// Raft/BoltDB store — see AppHost/configs/openbao/openbao.hcl) via OpenBao's online Raft
/// snapshot API. This is the officially-supported way to back up a live, concurrently-written
/// Raft store consistently; a raw file copy of the volume risks capturing a BoltDB file mid-write.
///
/// A snapshot captures everything in the vault — not just the KV secrets mount that
/// <see cref="OpenBaoBackup"/>'s logical export covers, but also auth backends, policies, and the
/// token store — so it's the authoritative backup. <see cref="OpenBaoBackup"/>'s KV export is kept
/// alongside it as a human-readable fallback.
/// </summary>
internal sealed class OpenBaoRaftSnapshot
{
    public async Task SaveToFileAsync(OpenBaoOptions options, string outputFile, CancellationToken cancellationToken)
    {
        using var client = OpenBaoHttp.NewClient(options);
        using var response = await client.GetAsync(
            "/v1/sys/storage/raft/snapshot", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        var temporary = outputFile + ".tmp";
        await using (var destination = File.Create(temporary))
            await response.Content.CopyToAsync(destination, cancellationToken);
        File.Move(temporary, outputFile, overwrite: true);
    }

    /// <summary>
    /// Restores a snapshot into the already-running, unsealed OpenBao it was pointed at. Uses
    /// snapshot-force: the plain restore endpoint rejects a snapshot whose Raft index isn't newer
    /// than the cluster's current state, which is exactly the case this exists for (recovering
    /// onto older data). Restoring replaces the vault's entire storage, including its token store
    /// and encryption keyring, with the snapshot's contents.
    /// </summary>
    public async Task RestoreFromFileAsync(OpenBaoOptions options, string inputFile, CancellationToken cancellationToken)
    {
        using var client = OpenBaoHttp.NewClient(options);
        await using var source = File.OpenRead(inputFile);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync("/v1/sys/storage/raft/snapshot-force", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
