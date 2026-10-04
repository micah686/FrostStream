using Conduit.NATS;
using DataBridge;
using Lite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Deployment;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI;

namespace UnitTests.Deployment;

public sealed class ModuleCompositionTests
{
    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(ModuleCompositionTests).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath()
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:SingleUserMode"] = "true",
            ["Worker:IncomingRoot"] = Path.Combine(Path.GetTempPath(), "froststream-module-tests", "incoming")
        });
        return builder;
    }

    [Test]
    public async Task Sqlite_Lite_Selects_Durable_Transport_After_All_Modules_Register()
    {
        var builder = Builder();
        builder.Configuration["Deployment:Mode"] = "Lite";
        builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(dir, "test.db");
        try
        {
            builder.Configuration["Auth:SingleUserMode"] = "false";
            builder.AddLiteModules();
            await using var provider = builder.Services.BuildServiceProvider();
            var hostedTypes = builder.Services.Where(d => d.ServiceType == typeof(IHostedService))
                .Select(d => d.ImplementationType).ToList();
            hostedTypes.ShouldNotContain(typeof(Quartz.QuartzHostedService));
            hostedTypes.IndexOf(typeof(global::DataBridge.Messaging.DownloadFlowStartupService))
                .ShouldBeLessThan(hostedTypes.IndexOf(typeof(global::Scheduler.Services.LiteSchedulerStartupService)));
            hostedTypes.IndexOf(typeof(global::DataBridge.Messaging.ScheduleCrudConsumerService))
                .ShouldBeLessThan(hostedTypes.IndexOf(typeof(global::Scheduler.Services.LiteSchedulerStartupService)));
            builder.Services.ShouldNotContain(d => d.ServiceType == typeof(ITopologySource));
            builder.Services.ShouldNotContain(d => d.ServiceType == typeof(NATS.Client.Core.INatsConnection));
            builder.Services.ShouldNotContain(d => d.ServiceType == typeof(global::WebAPI.Auth.NatsBffTicketStore));
            provider.GetRequiredService<Func<string, FrostStream.ApplicationContracts.IStagedObjectStore>>() ("manifests")
                .ShouldBeOfType<global::DataBridge.Persistence.Sqlite.SqliteStagedObjectStore>();
            provider.GetRequiredService<Shared.Secrets.ISecretStore>()
                .ShouldBeOfType<global::DataBridge.Persistence.Secrets.SqliteSecretStore>();
            provider.GetRequiredService<global::DataBridge.Persistence.Secrets.LocalSecretStoreOptions>()
                .KeyRingPath.ShouldBe(Path.Combine(dir, "test.db.keys"));
            builder.Services.ShouldNotContain(d => d.ImplementationType == typeof(Shared.Secrets.OpenBaoSecretStore));
            var publisher = provider.GetRequiredService<FrostStream.ApplicationContracts.IDurableJobPublisher>();
            publisher.ShouldBeOfType<global::DataBridge.Messaging.SqliteDurableTransport>();
            provider.GetRequiredService<FrostStream.ApplicationContracts.IDurableJobConsumer>().ShouldBeSameAs(publisher);
            provider.GetRequiredService<FrostStream.ApplicationContracts.IWorkerJobRoutes>().ShouldBeSameAs(publisher);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Full_Keeps_OpenBao_Secret_Store()
    {
        var builder = Builder();
        builder.Configuration["OpenBao:Token"] = "test-token";
        builder.AddLiteModules();
        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<Shared.Secrets.ISecretStore>().ShouldBeOfType<Shared.Secrets.OpenBaoSecretStore>();
        builder.Services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(Quartz.QuartzHostedService));
        builder.Services.ShouldNotContain(d => d.ImplementationType == typeof(global::Scheduler.Services.LiteSchedulerStartupService));
    }

    [Test]
    public void Deployment_Defaults_To_Full_And_Rejects_Invalid_Values()
    {
        DeploymentOptions.FromConfiguration(new ConfigurationBuilder().Build()).Mode.ShouldBe(DeploymentMode.Full);
        foreach (var invalid in new[] { "", "other", "1", "Full, Lite" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Deployment:Mode"] = invalid }).Build();
            Should.Throw<InvalidOperationException>(() => DeploymentOptions.FromConfiguration(configuration));
        }
    }

    [Test]
    public async Task Lite_Mode_Selects_Local_Requests_And_Events()
    {
        var builder = Builder();
        builder.Configuration["Deployment:Mode"] = "Lite";
        DeploymentOptions.FromConfiguration(builder.Configuration).Mode.ShouldBe(DeploymentMode.Lite);
        builder.AddLiteModules();
        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<FrostStream.ApplicationContracts.IMessageBus>()
            .ShouldBeOfType<Shared.Messaging.Adapters.LocalApplicationTransport>();
    }

    [Test]
    public async Task Combined_Modules_Are_Idempotent_And_Share_The_Controller_Assembly()
    {
        var builder = Builder();
        builder.AddLiteModules();
        var count = builder.Services.Count;
        builder.AddLiteModules();
        builder.Services.Count.ShouldBe(count);
        builder.Services.Count(d => d.ServiceType == typeof(ITopologySource) && d.ImplementationType == typeof(DownloadTopology)).ShouldBe(1);
        builder.Services.Count(d => d.ServiceType == typeof(FrostStream.ApplicationContracts.IMessageBus)).ShouldBe(1);
        foreach (var role in new[] { "databridge", "worker", "media-processor" })
            builder.Services.Count(d => d.ServiceType == typeof(IBackgroundRunReporter) && Equals(d.ServiceKey, role)).ShouldBe(1);
        builder.Services.ShouldContain(d => d.ServiceType == typeof(global::DataBridge.Flows.DownloadJobV2Flows));

        await using var app = builder.Build();
        var parts = app.Services.GetRequiredService<ApplicationPartManager>();
        parts.ApplicationParts.OfType<AssemblyPart>().Count(p => p.Assembly == typeof(WebAPIModule).Assembly).ShouldBe(1);
    }

    [Test]
    public async Task Full_And_Combined_Hosts_Discover_Identical_Controller_Routes()
    {
        var full = Builder();
        full.AddWebAPIModule();
        var combined = Builder();
        combined.AddLiteModules();
        await using var fullApp = full.Build();
        await using var combinedApp = combined.Build();
        Routes(fullApp).ShouldBe(Routes(combinedApp));
        Routes(combinedApp).ShouldContain("api/system/capabilities");
    }

    private static string[] Routes(WebApplication app) => app.Services
        .GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
        .OfType<ControllerActionDescriptor>().Select(a => a.AttributeRouteInfo?.Template ?? "")
        .Order(StringComparer.Ordinal).ToArray();

    [Test]
    public void Capabilities_Reflect_Configured_Optional_Integrations_And_Access_Mode()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SINGLE_USER_MODE"] = "true",
            ["LiveChat:Enabled"] = "true",
            ["PotBroker:Enabled"] = "true"
        }).Build();
        var result = SystemCapabilities.Resolve(new(DeploymentMode.Full), configuration);
        result.DeploymentMode.ShouldBe("Full");
        result.AccessManagement.Enabled.ShouldBeFalse();
        result.Integrations.LiveChat.ShouldBeTrue();
        result.Integrations.PotProvider.ShouldBeTrue();
        result.Backups.PointInTimeRecovery.ShouldBeTrue();
        var defaults = SystemCapabilities.Resolve(new(DeploymentMode.Full), new ConfigurationBuilder().Build());
        defaults.AccessManagement.Enabled.ShouldBeTrue();
        defaults.Integrations.LiveChat.ShouldBeFalse();
        defaults.Integrations.PotProvider.ShouldBeFalse();
    }
}
