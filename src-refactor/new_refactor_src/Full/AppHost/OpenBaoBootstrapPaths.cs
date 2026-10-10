namespace AppHost;

/// <summary>
/// Host location for the OpenBao development recovery material. This is deliberately separate
/// from the Raft volume so operators can back it up independently.
/// </summary>
internal static class OpenBaoBootstrapPaths
{
    public const string ComposeDefaultRoot = "./openbao-bootstrap";

    public static string HostRoot(string sharedStorageRoot)
    {
        var configured = Environment.GetEnvironmentVariable("FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(sharedStorageRoot, "openbao-bootstrap")
            : configured;

        if (!Path.IsPathRooted(root))
        {
            throw new InvalidOperationException(
                "FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT must be an absolute path when running AppHost.");
        }

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }
}
