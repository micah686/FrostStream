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
        var root = DeploymentRuntime.Current.Paths.OpenBaoBootstrapRoot;
        Directory.CreateDirectory(root);
        return root;
    }
}
