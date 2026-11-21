using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackupServiceNew.PgBackRestWrapper;
using Microsoft.Extensions.Caching.Memory;
using Shared.Backups;

namespace BackupServiceNew.Recovery;

internal sealed record WalAssessment(CheckState State, string Detail, DateTimeOffset ObservedAt);
internal sealed record RecoveryPlan(Guid Id, DateTimeOffset ExpiresAt, RecoveryMode Mode, DateTimeOffset? Target,
    BackupInfoDto Backup, string ArchiveId, int PostgresMajor, string Destination, bool Populated, long RequiredBytes, WalAssessment Wal)
{
    public string? ManifestHash { get; init; }
}
internal sealed record BackupPage(IReadOnlyList<BackupInfoDto> Items, int Number, int TotalPages, int Total);

internal sealed class RecoveryPlans(WrapperOptions options, BackupRepository repository, IMemoryCache cache)
{
    public const int PageSize = 20;
    public string Stanza => options.Stanza;

    public RecoveryPlan? Get(Guid id) => cache.Get<RecoveryPlan>(id);

    public BackupPage Browse(RepositoryInfo inventory, int page, string? query, string? type, DateOnly? from, DateOnly? to)
    {
        var matches = Newest(inventory).Where(backup =>
            (string.IsNullOrEmpty(type) || backup.Type == type)
            && (!from.HasValue || DateOnly.FromDateTime(backup.Finished.UtcDateTime) >= from)
            && (!to.HasValue || DateOnly.FromDateTime(backup.Finished.UtcDateTime) <= to)
            && (string.IsNullOrWhiteSpace(query) || backup.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Name(backup)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        var pages = Math.Max(1, (matches.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pages);
        return new(matches.Skip((page - 1) * PageSize).Take(PageSize).Select(repository.Describe).ToArray(),
            page, pages, matches.Length);
    }

    public async Task<RecoveryPlan> CreateLogicalAsync(LogicalBackups logical, string label, CancellationToken ct)
    {
        var backup = logical.List().SingleOrDefault(b => b.Label == label && b.Mode == "logical")
            ?? throw new InvalidOperationException("Choose an available logical backup.");
        var hash = await logical.ManifestHashAsync(label, ct);
        await logical.VerifyAsync(label, false, new Progress<OutputLine>(), ct);
        if (hash != await logical.ManifestHashAsync(label, ct)) throw new InvalidOperationException("The backup changed during verification.");
        var plan = new RecoveryPlan(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30), RecoveryMode.Consistency, null,
            backup, "", 0, options.SocketPath, true, 0, new(CheckState.Passed, "Logical backups do not replay WAL.", DateTimeOffset.UtcNow)) { ManifestHash = hash };
        cache.Set(plan.Id, plan, plan.ExpiresAt);
        return plan;
    }

    public RecoveryPlan Create(ReadinessReport readiness, RecoveryMode mode, string? label, DateTimeOffset? target)
    {
        if (!readiness.Ready || readiness.Repository is not { } inventory)
            throw new InvalidOperationException("Run the readiness checks and resolve all blockers before choosing recovery.");

        if (mode is not (RecoveryMode.Latest or RecoveryMode.Timestamp or RecoveryMode.Consistency))
            throw new ArgumentException("Choose a recovery method.");
        if (mode == RecoveryMode.Consistency && string.IsNullOrEmpty(label))
            throw new ArgumentException("Choose a backup to recover.");
        if (mode == RecoveryMode.Timestamp && (target is null || target > DateTimeOffset.UtcNow))
            throw new ArgumentException("Enter a recovery time in the past with an explicit UTC offset.");

        var candidates = Newest(inventory).Where(backup => mode switch
        {
            RecoveryMode.Consistency => backup.Label == label,
            RecoveryMode.Timestamp => backup.Finished <= target,
            _ => true
        });
        var index = inventory.Backups.ToDictionary(backup => backup.Label, StringComparer.Ordinal);
        string? rejection = null;
        foreach (var backup in candidates)
        {
            try
            {
                var plan = Pin(readiness, index, backup, mode, mode == RecoveryMode.Timestamp ? target : null);
                cache.Set(plan.Id, plan, plan.ExpiresAt);
                return plan;
            }
            catch (InvalidOperationException ex)
            {
                rejection ??= ex.Message;
            }
        }

        throw new InvalidOperationException(rejection ?? (mode == RecoveryMode.Timestamp
            ? "No backup completed by the requested time."
            : "No backup matches this recovery selection."));
    }

    public void Revalidate(RecoveryPlan plan, ReadinessReport readiness)
    {
        var blocker = readiness.Checks.FirstOrDefault(check => check.State != CheckState.Passed);
        if (blocker is not null)
            throw new InvalidOperationException($"{blocker.Name}. {blocker.Detail} {blocker.Remediation}");

        var index = readiness.Repository!.Backups.ToDictionary(backup => backup.Label, StringComparer.Ordinal);
        if (!index.TryGetValue(plan.Backup.Label, out var backup))
            throw new InvalidOperationException("The pinned backup is no longer in the repository. Create a new plan.");
        var current = Pin(readiness, index, backup, plan.Mode, plan.Target);
        if (current.Populated != plan.Populated || current.PostgresMajor != plan.PostgresMajor ||
            current.Destination != plan.Destination || current.RequiredBytes != plan.RequiredBytes ||
            current.ArchiveId != plan.ArchiveId || current.Backup.WalStart != plan.Backup.WalStart ||
            current.Backup.WalStop != plan.Backup.WalStop || current.Backup.CompletedAt != plan.Backup.CompletedAt ||
            !current.Backup.Dependencies!.SequenceEqual(plan.Backup.Dependencies!) ||
            current.Backup.OpenBaoExportPresent != plan.Backup.OpenBaoExportPresent)
            throw new InvalidOperationException("The pinned backup or restore destination changed. Create a new plan.");
    }

    public string ValidateRollback(RecoveryPlan plan, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Enter an absolute path to the independently preserved PGDATA copy.");
        var rollback = Paths.Absolute(directory.Trim());
        Paths.NoLinks(rollback);
        foreach (var path in new[] { plan.Destination, options.RepositoryPath, options.StatePath })
            Paths.Disjoint(rollback, path);
        if (RecoveryReadiness.FindMount(rollback) == RecoveryReadiness.FindMount(plan.Destination)
            || RecoveryReadiness.FindMount(rollback) is null)
            throw new ArgumentException("Mount the preserved copy separately from the PostgreSQL data volume.");
        var version = Path.Combine(rollback, "PG_VERSION");
        var basePath = Path.Combine(rollback, "base");
        Paths.NoLinks(version);
        Paths.NoLinks(basePath);
        if (!File.Exists(version) || !Directory.Exists(basePath))
            throw new ArgumentException("The preserved copy must contain PG_VERSION and the base directory.");
        return rollback;
    }

    public static bool TryTarget(string? input, out DateTimeOffset target)
    {
        target = default;
        return input is { Length: <= 40 } && Regex.IsMatch(input,
            @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,6})?)?(?:Z|[+-]\d{2}:\d{2})\z")
            && DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out target);
    }

    private RecoveryPlan Pin(ReadinessReport readiness, Dictionary<string, BackupInfo> index, BackupInfo backup,
        RecoveryMode mode, DateTimeOffset? target)
    {
        var inventory = readiness.Repository!;
        var dbId = DatabaseId(backup);
        var db = inventory.Metadata.GetProperty("db").EnumerateArray()
            .SingleOrDefault(db => db.GetProperty("id").GetInt32() == dbId);
        var postgresMajor = db.ValueKind == JsonValueKind.Object
            ? RecoveryReadiness.Major(db.GetProperty("version").GetString() ?? "") : null;
        if (postgresMajor is null || postgresMajor != readiness.PostgresMajor)
            throw new InvalidOperationException($"Backup {backup.Label} requires a different or unknown PostgreSQL major version.");

        var chain = ResolveChain(backup, dbId, index);
        var summary = repository.Describe(backup) with { Dependencies = chain };
        var requiredBytes = checked(backup.DatabaseBytes + options.MinimumFreeBytes);
        if (backup.DatabaseBytes <= 0 || readiness.FreeBytes < requiredBytes)
            throw new InvalidOperationException($"Backup {backup.Label} needs {RecoveryConsole.Bytes(requiredBytes)} of free space including headroom. The destination has {RecoveryConsole.Bytes(readiness.FreeBytes)}.");

        var archiveId = inventory.Metadata.TryGetProperty("archive", out var archives) && archives.ValueKind == JsonValueKind.Array
            ? archives.EnumerateArray().FirstOrDefault(archive => archive.TryGetProperty("database", out var database)
                && database.GetProperty("id").GetInt32() == dbId
                && (!database.TryGetProperty("repo-key", out var key) || key.GetInt32() == 1))
            : default;
        var generation = archiveId.ValueKind == JsonValueKind.Object ? archiveId.GetProperty("id").GetString() ?? "" : "";
        var wal = AssessWal(generation, summary.WalStart, summary.WalStop, mode);
        if (wal.State == CheckState.Failed) throw new InvalidOperationException(wal.Detail);

        return new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30), mode, target, summary,
            generation, postgresMajor.Value, Paths.Absolute(options.DataPath), readiness.Populated, requiredBytes, wal);
    }

    private static string[] ResolveChain(BackupInfo backup, int dbId, Dictionary<string, BackupInfo> index)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        Visit(backup.Label);
        dependencies.Remove(backup.Label);
        return dependencies.Order(StringComparer.Ordinal).ToArray();

        void Visit(string label)
        {
            if (visiting.Contains(label))
                throw new InvalidOperationException($"Backup {backup.Label} has a cyclic dependency.");
            if (!index.TryGetValue(label, out var item) || item.HasError || DatabaseId(item) != dbId)
                throw new InvalidOperationException($"Required backup {label} is missing, reports an error, or belongs to another cluster.");
            if (!dependencies.Add(label)) return;
            visiting.Add(label);
            if (item.Type is BackupTypes.Differential or BackupTypes.Incremental)
            {
                var separator = label.IndexOf('_');
                if (separator < 0 || !index.TryGetValue(label[..separator], out var full) || full.Type != BackupTypes.Full)
                    throw new InvalidOperationException($"Backup {label} is missing its full base.");
                Visit(full.Label);
                if (item.Metadata.TryGetProperty("prior", out var prior) && prior.ValueKind == JsonValueKind.String)
                    Visit(prior.GetString()!);
                else if (item.Type == BackupTypes.Incremental)
                    throw new InvalidOperationException($"Incremental backup {label} has no preceding backup in its metadata.");
            }
            else if (item.Type != BackupTypes.Full)
                throw new InvalidOperationException($"Backup {label} has an unsupported backup type.");
            if (item.Metadata.TryGetProperty("reference", out var references) && references.ValueKind == JsonValueKind.Array)
                foreach (var reference in references.EnumerateArray()) Visit(reference.GetString()!);
            visiting.Remove(label);
        }
    }

    private WalAssessment AssessWal(string generation, string? start, string? stop, RecoveryMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        if (start is null || stop is null || !Regex.IsMatch(start, @"\A[0-9A-F]{24}\z") ||
            !Regex.IsMatch(stop, @"\A[0-9A-F]{24}\z") || !Regex.IsMatch(generation, @"\A\d+(?:\.\d+)?-\d+\z"))
            return new(CheckState.Unknown, "The backup's required WAL bounds or archive generation could not be identified.", now);
        if (start[..8] != stop[..8] || string.CompareOrdinal(start, stop) > 0)
            return new(CheckState.Failed, "The pinned backup has incompatible timeline or WAL bounds.", now);

        try
        {
            var root = Path.Combine(options.RepositoryPath, "archive", options.Stanza, generation);
            Paths.NoLinks(root);
            var first = Convert.ToUInt32(start[16..], 16);
            var last = Convert.ToUInt32(stop[16..], 16);
            var bounded = start[..16] == stop[..16] && last - first < 256;
            IEnumerable<string> required = bounded
                ? Enumerable.Range(0, (int)(last - first) + 1).Select(i => start[..16] + (first + (uint)i).ToString("X8", CultureInfo.InvariantCulture))
                : new[] { start, stop };
            // Read each bounded WAL prefix directory once. Match pgBackRest's segment and checksum filename format.
            foreach (var group in required.GroupBy(name => name[..16]))
            {
                var path = Path.Combine(root, group.Key);
                Paths.NoLinks(path);
                var names = Directory.Exists(path)
                    ? Directory.EnumerateFiles(path).Take(4097).ToArray()
                    : [];
                if (names.Length > 4096)
                    return new(CheckState.Unknown, "Archive inventory exceeded the check's entry budget.", now);
                var available = names.Select(Path.GetFileName)
                    .Where(name => name is not null && Regex.IsMatch(name, @"\A[0-9A-F]{24}-[0-9A-Fa-f]{40}(?:\.\w+)?\z"))
                    .Select(name => name![..24]).ToHashSet(StringComparer.Ordinal);
                var missing = group.FirstOrDefault(name => !available.Contains(name));
                if (missing is not null)
                    return new(CheckState.Failed, $"Required WAL segment {missing} is missing from archive generation {generation}.", now);
            }

            var scope = bounded
                ? $"Found required WAL filenames from {start} through {stop} in {generation}."
                : $"Found the WAL endpoints {start} and {stop} in {generation}. Interior segments remain unchecked.";
            return new(CheckState.Unknown, scope + " WAL readability remains unknown." +
                (mode == RecoveryMode.Consistency ? "" : " Archive coverage beyond backup consistency and target reachability remain unknown."), now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(CheckState.Unknown, "The required WAL archive could not be fully inventoried.", now);
        }
    }

    private static IOrderedEnumerable<BackupInfo> Newest(RepositoryInfo inventory) =>
        inventory.Backups.OrderByDescending(backup => backup.Finished).ThenByDescending(backup => backup.Label, StringComparer.Ordinal);

    private static int DatabaseId(BackupInfo backup) => backup.Metadata.GetProperty("database").GetProperty("id").GetInt32();
    private static string? Name(BackupInfo backup) =>
        backup.Metadata.TryGetProperty("annotation", out var annotation) && annotation.ValueKind == JsonValueKind.Object
            && annotation.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
}
