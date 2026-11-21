using BackupService;
using BackupService.PgBackRest;
using BackupServiceNew;
using BackupServiceNew.PgBackRestWrapper;
using Microsoft.Extensions.Logging.Abstractions;

RecoveryConsoleTests.Run();

var root = Path.Combine(Path.GetTempPath(), "backup-bundle-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var settings = new BackupServiceOptions { Directory = root };
    var runner = new FakeRunner();
    var service = new LogicalBackups(settings, runner, new OpenBaoPairing(settings, NullLogger<OpenBaoPairing>.Instance));
    var progress = new Progress<OutputLine>();
    var backup = await service.CreateAsync("before upgrade", Guid.NewGuid(), false, false, false, progress, default);
    if (service.List().Single().Label != backup.Label) throw new Exception("Completed bundle not discoverable.");
    if (runner.Commands.Count != 3 || runner.Commands.Any(c => c.Executable != "pg_dump" || !c.Arguments.Contains("--format=directory"))) throw new Exception("Expected three directory dumps.");
    await service.VerifyAsync(backup.Label, false, progress, default);
    await service.VerifyAsync(backup.Label, true, progress, default);
    if (runner.Commands.Count(c => c.Executable == "pg_restore") != 3) throw new Exception("Deep verify did not restore all databases.");
    if (runner.Commands.Where(c => c.Executable == "pg_restore").Any(c => c.Arguments.Contains(settings.PostgresSocketDirectory))) throw new Exception("Deep verify targeted live database.");
    var restoreStart = runner.Commands.Count;
    await service.RestoreAsync(backup.Label, Guid.NewGuid(), progress, default);
    var restoreCommands = runner.Commands.Skip(restoreStart).ToArray();
    if (restoreCommands.Count(c => c.Executable == "dropdb") != settings.ExpectedDatabases.Length
        || restoreCommands.Count(c => c.Executable == "pg_restore" && c.Arguments.Contains("--exit-on-error")) != settings.ExpectedDatabases.Length)
        throw new Exception("Logical restore must replace all databases.");
    if (restoreCommands.Where(c => c.Executable == "dropdb").Any(c => !c.Arguments.Contains("--force")))
        throw new Exception("Logical restore must disconnect existing database sessions.");
    if (Array.FindIndex(restoreCommands, c => c.Executable == "dropdb") < settings.ExpectedDatabases.Length)
        throw new Exception("Dump preflight must precede replacement.");
    await File.AppendAllTextAsync(Path.Combine(root, backup.Label, "postgres_logical", settings.ExpectedDatabases[0], "toc.dat"), "corrupt");
    await MustFail<InvalidDataException>(() => service.VerifyAsync(backup.Label, false, progress, default));
    await MustFail<ArgumentException>(() => service.VerifyAsync("../escape", false, progress, default));
    await MustFail<InvalidOperationException>(() => service.CreateAsync("missing keys", Guid.NewGuid(), false, true, false, progress, default));
    var corruptStart = runner.Commands.Count;
    await MustFail<InvalidDataException>(() => service.RestoreAsync(backup.Label, Guid.NewGuid(), progress, default));
    if (runner.Commands.Count != corruptStart) throw new Exception("Corrupt backups must fail before invoking tools.");
    var repository = Path.Combine(root, "test-repository");
    Directory.CreateDirectory(repository);
    File.WriteAllText(Path.Combine(repository, "backup.info"), "repository metadata");
    var physical = backup with { Label = "20260101-000000F", Mode = "physical" };
    await service.CreatePhysicalBundleAsync(physical, Guid.NewGuid(), repository, false, false, false, default);
    var bundle = service.FindPhysicalBundle(physical.Label) ?? throw new Exception("Physical bundle missing.");
    await service.VerifyAsync(Path.GetFileName(bundle), false, progress, default);
    File.WriteAllText(Path.Combine(repository, "backup.info"), "changed repository");
    await service.VerifyAsync(Path.GetFileName(bundle), false, progress, default);
    if (File.ReadAllText(Path.Combine(bundle, "postgres_physical", "pgbackrest", "backup.info")) != "repository metadata") throw new Exception("Physical copy is not independent.");
    var keysRoot = Path.Combine(Path.GetTempPath(), "restore-keys-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(keysRoot);
    try
    {
        var keyPath = Path.Combine(keysRoot, "keys");
        Directory.CreateDirectory(keyPath);
        await File.WriteAllTextAsync(Path.Combine(keyPath, "key.xml"), "preserved key");
        var keySettings = new BackupServiceOptions { Directory = Path.Combine(keysRoot, "backups"), DataProtectionKeysPath = keyPath };
        var keyService = new LogicalBackups(keySettings, runner, new OpenBaoPairing(keySettings, NullLogger<OpenBaoPairing>.Instance));
        var keyBackup = await keyService.CreateAsync("keys", Guid.NewGuid(), false, false, true, progress, default);
        await File.WriteAllTextAsync(Path.Combine(keyPath, "key.xml"), "changed key");
        await keyService.RestoreOtherAsync(keyBackup.Label, [".dataprotection"], progress, default);
        if (await File.ReadAllTextAsync(Path.Combine(keyPath, "key.xml")) != "preserved key") throw new Exception("Key restore failed.");
        await MustFail<InvalidOperationException>(() => keyService.RestoreOtherAsync(keyBackup.Label, ["unknown"], progress, default));
        await File.AppendAllTextAsync(Path.Combine(keySettings.Directory, keyBackup.Label, ".dataprotection", "key.xml"), "tampered");
        await MustFail<InvalidDataException>(() => keyService.RestoreOtherAsync(keyBackup.Label, [".dataprotection"], progress, default));
    }
    finally { Directory.Delete(keysRoot, true); }
    runner.FailDump = true;
    await MustFail<CommandFailedException>(() => service.CreateAsync("failed", Guid.NewGuid(), false, false, false, progress, default));
    if (service.List().Count() != 2) throw new Exception("Incomplete bundle listed as complete.");
    Console.WriteLine("Passed: directory dumps, discovery, hashes, isolated deep restore, corruption, traversal, missing keys, incomplete backup exclusion, all-database restore and verified key restore.");
}
finally { Directory.Delete(root, true); }

static async Task MustFail<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
sealed class FakeRunner : ICommandRunner
{
    public List<CommandSpec> Commands { get; } = [];
    public bool FailDump { get; set; }
    public Task<CommandResult> RunAsync(CommandSpec command, IProgress<OutputLine>? progress = null, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        if (command.Executable == "pg_dump" && !FailDump)
        {
            var args = command.Arguments.ToList();
            var directory = args[args.IndexOf("--file") + 1];
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "toc.dat"), "test dump inventory");
        }
        return Task.FromResult(new CommandResult(Guid.NewGuid(), command, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, command.Executable == "pg_dump" && FailDump ? 1 : 0, command.Arguments.Contains("SELECT current_setting('data_directory');") ? new BackupServiceOptions().PgDataPath : "", ""));
    }
}
