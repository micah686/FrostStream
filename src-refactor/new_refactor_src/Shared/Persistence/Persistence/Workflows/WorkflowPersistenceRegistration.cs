using Cleipnir.Flows.AspNet;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Messaging;
using Microsoft.Extensions.DependencyInjection;
namespace DataBridge.Persistence.Workflows;
public interface IWorkflowStoreFactory { IFunctionStore Create(); }
public static class WorkflowPersistenceRegistration
{
    public static FlowsConfigurator UseApplicationStore(this FlowsConfigurator configurator)
    {
        configurator.Services.AddSingleton<IFunctionStore>(sp => new StartupGatedFunctionStore(
            sp.GetRequiredService<IWorkflowStoreFactory>().Create(), sp.GetRequiredService<DownloadFlowStartupState>()));
        return configurator;
    }
}
