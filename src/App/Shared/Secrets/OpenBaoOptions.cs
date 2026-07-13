namespace Shared.Secrets;

public sealed class OpenBaoOptions
{
    public const string SectionName = "OpenBao";

    public string Address { get; set; } = "http://127.0.0.1:25400";

    public string? Token { get; set; }

    public string? RoleId { get; set; }

    public string? SecretId { get; set; }

    public string KvMount { get; set; } = "secret";
}
