using Microsoft.Extensions.Configuration;
using Shared.Auth;

namespace Shared.Deployment;

/// <summary>Public feature availability, not credentials, addresses, or a dependency health check.</summary>
public sealed record SystemCapabilities(
    string DeploymentMode,
    IntegrationCapabilities Integrations,
    AccessManagementCapabilities AccessManagement,
    BackupCapabilities Backups)
{
    public static SystemCapabilities Resolve(DeploymentOptions deployment, IConfiguration configuration) => new(
        deployment.Mode.ToString(),
        new(Search: true,
            LiveChat: configuration.GetValue("LiveChat:Enabled", false),
            PotProvider: configuration.GetValue("PotBroker:Enabled", false) || configuration.GetValue("PotProvider:Enabled", false)),
        new(Enabled: !AuthMode.IsSingleUserMode(configuration)),
        deployment.Mode == Shared.Deployment.DeploymentMode.Lite
            ? new(Provider: "SQLite", Full: true, Differential: false, Incremental: false, Verification: true, PointInTimeRecovery: false, DeepVerification: false)
            : new(Provider: "PostgreSQL", Full: true, Differential: true, Incremental: true, Verification: true, PointInTimeRecovery: true));
}

public sealed record IntegrationCapabilities(bool Search, bool LiveChat, bool PotProvider);
public sealed record AccessManagementCapabilities(bool Enabled);
public sealed record BackupCapabilities(string Provider, bool Full, bool Differential, bool Incremental, bool Verification, bool PointInTimeRecovery, bool DeepVerification = true);
