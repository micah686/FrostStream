namespace BackupService;

internal sealed record OpenBaoOptions(string Address, string? Token, string KvMount)
{
    public static OpenBaoOptions From(BackupServiceOptions options)
        => new(options.OpenBaoAddress, options.OpenBaoToken, options.OpenBaoKvMount);
}
