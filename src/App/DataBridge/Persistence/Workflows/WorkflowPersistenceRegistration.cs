using Cleipnir.Flows.AspNet;
using Cleipnir.Flows.PostgresSql;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DataBridge.Persistence.Workflows;

public static class WorkflowPersistenceRegistration
{
    public static FlowsConfigurator UsePersistenceStore(this FlowsConfigurator configurator, PersistenceOptions options, string postgresConnectionString)
    {
        if (options.Provider == PersistenceProvider.Postgres)
            return configurator.UsePostgresStore(new NpgsqlConnectionStringBuilder(postgresConnectionString) { SearchPath = "cleipnir,public" }.ConnectionString);
        configurator.Services.AddSingleton<IFunctionStore>(sp => sp.GetRequiredService<SqliteFunctionStore>());
        return configurator;
    }
}
