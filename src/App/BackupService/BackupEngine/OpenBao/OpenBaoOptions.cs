namespace BackupService;

internal sealed record OpenBaoOptions(string Address, string? Token, string KvMount)
{
    public static OpenBaoOptions From(CliOptions options)
        => new(
            options.Get("openbao-address")
            ?? Environment.GetEnvironmentVariable("OPENBAO_ADDR")
            ?? Environment.GetEnvironmentVariable("OpenBao__Address")
            ?? "http://127.0.0.1:25400",
            options.Get("openbao-token")
            ?? Environment.GetEnvironmentVariable("OPENBAO_TOKEN")
            ?? Environment.GetEnvironmentVariable("OpenBao__Token"),
            options.Get("openbao-kv-mount")
            ?? Environment.GetEnvironmentVariable("OPENBAO_KV_MOUNT")
            ?? Environment.GetEnvironmentVariable("OpenBao__KvMount")
            ?? "secret");

    public static OpenBaoOptions From(BackupServiceOptions options)
        => new(options.OpenBaoAddress, options.OpenBaoToken, options.OpenBaoKvMount);
}
