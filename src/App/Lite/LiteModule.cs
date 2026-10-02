using DataBridge;
using MediaProcessor;
using Scheduler;
using WebAPI;
using Worker;

namespace Lite;

/// <summary>
/// Single-process composition root. Phase 1 uses Full infrastructure; subsequent phases supply
/// SQLite and local adapters. Controllers and application handlers remain in the shared modules.
/// </summary>
public static class LiteModule
{
    public static IHostApplicationBuilder AddLiteModules(this IHostApplicationBuilder builder)
    {
        // DataBridge owns topology provisioning and registers reconciliation before consumers.
        builder.AddDataBridgeModule();
        builder.AddMediaProcessorModule();
        builder.AddWorkerModule();
        builder.AddWebAPIModule();
        builder.AddSchedulerModule();
        return builder;
    }
}
