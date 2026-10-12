using DataBridge.Search;

namespace Shared.Deployment;

public enum OptionalIntegrationState { Disabled, Starting, Ready, Failed }
public sealed record IntegrationStatusRequest;
public sealed record IntegrationStatusSnapshot(SearchState Search, OptionalIntegrationState LiveChatSchema);
public static class IntegrationStatusSubjects { public const string Status = "system.integrations.status"; }

/// <summary>Startup schema state, separate from feature configuration and application readiness.</summary>
public sealed class LiveChatInitializationStatus(OptionalIntegrationState initial)
{
    private int state = (int)initial;
    public OptionalIntegrationState State => (OptionalIntegrationState)Volatile.Read(ref state);
    public void Set(OptionalIntegrationState value) => Volatile.Write(ref state, (int)value);
}
