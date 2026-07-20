using Cleipnir.Flows;
using Shared.Messaging;

namespace DataBridge.Flows;

/// <summary>
/// Tombstone for the pre-V2 Cleipnir function type. It is intentionally never invoked or hosted;
/// the generated control-panel client exists only so the one-time V2 cutover can delete persisted
/// legacy instances by their original function identity.
/// </summary>
[GenerateFlows]
public sealed class DownloadArchiveFlow : Flow<DownloadRequested>
{
    public override Task Run(DownloadRequested param) =>
        throw new NotSupportedException("The legacy download flow was retired by Download Flow V2.");
}
