using System.Text.Json;

namespace BackupServiceNew.PgBackRestWrapper;

/// <summary>Evidence collected for a backup. Unknown coverage is never a recovery guarantee.</summary>
public sealed record CoverageAssessment(DateTimeOffset ObservedAt, string BackupLabel, string? PostgresVersion,
    IReadOnlyList<string> BackupDependencies, string? WalStart, string? WalStop, bool BackupVerified,
    string Verdict, IReadOnlyList<string> Reasons);

public sealed partial class PgBackRestClient
{
    /// <summary>Inspects a selected backup and verifies it. WAL continuity and target reachability remain unknown.</summary>
    /// <summary>Assesses backup metadata and verifies a selected backup; archive continuity remains unproven.</summary>
    /// <param name="backupLabel">Label of the backup to assess.</param><param name="cancellationToken">Token used to cancel assessment.</param>
    public Task<CoverageAssessment> AssessCoverageAsync(string backupLabel, CancellationToken cancellationToken = default) =>
        Exclusive(async () =>
        {
            Label(backupLabel);
            var repository = await GetRepositoryAsync(cancellationToken);
            if (repository.StatusCode != 0) throw new InvalidOperationException($"Repository is unavailable: {repository.StatusMessage}");
            var backup = repository.Backups.SingleOrDefault(b => b.Label == backupLabel && !b.HasError)
                ?? throw new InvalidOperationException("Selected backup is missing or reports an error.");
            var dependencies = new List<string>();
            if (backup.Metadata.TryGetProperty("reference", out var refs) && refs.ValueKind == JsonValueKind.Array)
                foreach (var item in refs.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } reference) dependencies.Add(reference);
            var reasons = new List<string>();
            foreach (var reference in dependencies)
                if (!repository.Backups.Any(b => b.Label == reference && !b.HasError))
                    reasons.Add($"Required backup {reference} is missing or reports an error.");
            string? version = null;
            if (backup.Metadata.TryGetProperty("database", out var dbRef) && dbRef.TryGetProperty("id", out var id) &&
                repository.Metadata.TryGetProperty("db", out var databases) && databases.ValueKind == JsonValueKind.Array)
                foreach (var db in databases.EnumerateArray())
                    if (db.TryGetProperty("id", out var dbId) && dbId.GetInt32() == id.GetInt32() && db.TryGetProperty("version", out var v)) version = v.GetString();
            string? start = null, stop = null;
            if (backup.Metadata.TryGetProperty("archive", out var archive) && archive.ValueKind == JsonValueKind.Object)
            {
                if (archive.TryGetProperty("start", out var a) && a.ValueKind == JsonValueKind.String) start = a.GetString();
                if (archive.TryGetProperty("stop", out var b) && b.ValueKind == JsonValueKind.String) stop = b.GetString();
            }
            var capability = await Compatibility(cancellationToken);
            var verified = false;
            if (!capability.VerifySetAvailable) reasons.Add("Selected-backup verification is not supported by this pgBackRest version.");
            else
            {
                var result = await Run(options.PgBackRest, Arguments("verify", "--output=text", $"--set={backupLabel}"), cancellationToken, requireSuccess: false);
                cancellationToken.ThrowIfCancellationRequested();
                verified = result.Success;
                if (!verified) reasons.Add("Selected-backup verification failed; inspect command diagnostics.");
            }
            reasons.Add("Archive continuity, timeline history and target reachability have not been assessed.");
            var assessment = new CoverageAssessment(DateTimeOffset.UtcNow, backupLabel, version, dependencies, start, stop,
                verified, "Unknown", reasons);
            await reports.WriteAsync($"coverage-{Guid.NewGuid():N}.json", assessment);
            return assessment;
        }, cancellationToken);
}
