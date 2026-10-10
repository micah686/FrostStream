using Cleipnir.Flows.AspNet;
using Cleipnir.Flows.PostgresSql;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using DataBridge.Messaging;

namespace DataBridge.Persistence.Workflows;

public static class WorkflowPersistenceRegistration
{
    public static FlowsConfigurator UsePersistenceStore(this FlowsConfigurator configurator, PersistenceOptions options, string postgresConnectionString)
    {
        if (options.Provider == PersistenceProvider.Postgres)
            configurator.UsePostgresStore(new NpgsqlConnectionStringBuilder(postgresConnectionString) { SearchPath = "cleipnir,public" }.ConnectionString);
        else
            configurator.Services.AddSingleton<IFunctionStore>(sp => sp.GetRequiredService<SqliteFunctionStore>());
        var descriptor = configurator.Services.Last(d => d.ServiceType == typeof(IFunctionStore));
        configurator.Services.Remove(descriptor);
        configurator.Services.AddSingleton<IFunctionStore>(sp => new StartupGatedFunctionStore(
            (IFunctionStore)(descriptor.ImplementationInstance ?? descriptor.ImplementationFactory!(sp)),
            sp.GetRequiredService<DownloadFlowStartupState>()));
        return configurator;
    }
}
