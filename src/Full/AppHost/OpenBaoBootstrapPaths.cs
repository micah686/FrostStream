namespace AppHost;

/// <summary>
/// Host location for the OpenBao development recovery material. This is deliberately separate
/// from the Raft volume so operators can back it up independently.
/// </summary>
internal static class OpenBaoBootstrapPaths
{
    public const string ComposeDefaultRoot = "./openbao-bootstrap";

    public static string HostRoot(string sharedStorageRoot, string appHostDirectory)
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

        // Before Lite and Full received separate storage roots, Full kept its
        // recovery key beside the checkout's shared data directory.
        if (string.IsNullOrWhiteSpace(configured))
        {
            var legacyRoot = Path.GetFullPath(Path.Combine(appHostDirectory, "..", "..", "data", "openbao-bootstrap"));
            var legacyFile = Path.Combine(legacyRoot, "init.env");
            var currentFile = Path.Combine(root, "init.env");
            if (!File.Exists(currentFile) && File.Exists(legacyFile))
            {
                File.Copy(legacyFile, currentFile);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(currentFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        return root;
    }
}
