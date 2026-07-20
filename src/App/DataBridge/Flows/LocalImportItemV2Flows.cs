using Cleipnir.Flows;
using Shared.Messaging;

namespace DataBridge.Flows;

public sealed class LocalImportItemV2Flows(FlowsContainer flowsContainer)
    : Flows<LocalImportItemFlow, ImportSessionItemImportRequested>("LocalImportItemFlowV2", flowsContainer);
