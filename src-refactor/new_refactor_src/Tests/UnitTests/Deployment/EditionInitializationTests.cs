using FrostStream.Full.Infrastructure;
using FrostStream.Full.Security;
using FrostStream.Lite.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Deployment;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class EditionInitializationTests
{
    [Test]
    public void Local_And_Distributed_Durable_Routes_Agree()
    {
        Conduit.NATS.ITopologySource[] sources = [new NatsDownloadTopology(), new NatsArtifactStorageTopology(),
            new NatsLocalImportTopology(), new NatsPlaylistTopology(), new NatsBackgroundJobsTopology()];
        var distributed = sources.SelectMany(source => source.GetConsumers())
            .Select(route => $"{route.StreamName.Value}/{route.DurableName.Value}/{route.FilterSubject}").Order().ToArray();
        var local = Shared.Messaging.ApplicationJobRoutes.All
            .Select(route => $"{route.Stream}/{route.Consumer}/{route.Subject}").Order().ToArray();
        local.ShouldBe(distributed);
    }

    [Test]
    [Arguments("SINGLE_USER_MODE", "true")]
    [Arguments("SINGLE_USER_MODE", "false")]
    [Arguments("Auth:SingleUserMode", "true")]
    [Arguments("Auth:SingleUserMode", "false")]
    [Arguments("AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION", "true")]
    [Arguments("Auth:AllowSingleUserModeInProduction", "false")]
    public void Full_Rejects_Every_Former_Single_User_Configuration_Path(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[key] = value;
        Should.Throw<InvalidOperationException>(() => builder.AddFullMessaging()).Message.ShouldContain(key);
    }

    [Test]
    public void Edition_Providers_Cannot_Be_Registered_In_The_Other_Host()
    {
        var lite = Host.CreateApplicationBuilder();
        lite.Configuration["Deployment:Mode"] = "Lite";
        Should.Throw<InvalidOperationException>(() => lite.AddFullMessaging());
        Should.Throw<InvalidOperationException>(() => lite.AddFullPersistence());
        Should.Throw<InvalidOperationException>(() => lite.AddFullSecurity());
        Should.Throw<InvalidOperationException>(() => Shared.Secrets.ServiceCollectionExtensions.AddOpenBaoSecretStore(lite.Services, lite.Configuration));
        Should.Throw<InvalidOperationException>(() => lite.AddFullBackupClient());
        var full = Host.CreateApplicationBuilder();
        Should.Throw<InvalidOperationException>(() => full.AddLitePersistence());
        Should.Throw<InvalidOperationException>(() => full.AddLiteIdentity());
        Should.Throw<InvalidOperationException>(() => full.AddLiteMessaging());
        Should.Throw<InvalidOperationException>(() => full.AddLiteSecretStore());
        Should.Throw<InvalidOperationException>(() => full.AddLiteBackupClient());
        Should.Throw<InvalidOperationException>(() => full.AddLiteStagedStorage());
    }

    [Test]
    public void Full_Requires_Identity_And_Authorization_Configuration()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        Should.Throw<InvalidOperationException>(() => builder.AddFullSecurity()).Message.ShouldContain("Auth:Authority");
        var configured = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        configured.Configuration["Auth:Authority"] = "https://identity.example";
        configured.Configuration["Auth:PublicOrigin"] = "https://app.example";
        configured.Configuration["Auth:ClientSecret"] = "test";
        Should.Throw<InvalidOperationException>(() => configured.AddFullSecurity()).Message.ShouldContain("OpenFga:Endpoint");
    }

    [Test]
    public async Task Initialization_Is_Lazy_Ordered_And_Only_Runs_Once_For_Concurrent_Callers()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddDeployment();
        var events = new List<int>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("dependent", 20, (_, _) =>
        {
            events.ShouldBe(new[] { 10 }); events.Add(20); return Task.CompletedTask;
        }));
        builder.Services.AddSingleton(new ApplicationInitializationStep("schema", 10, async (_, ct) =>
        {
            await Task.Delay(10, ct); events.Add(10);
        }));
        using var host = builder.Build();
        events.ShouldBeEmpty();
        await Task.WhenAll(host.InitializeApplicationAsync(), host.InitializeApplicationAsync());
        events.ShouldBe(new[] { 10, 20 });
    }

    [Test]
    public async Task Cancelled_Or_Failed_Initialization_Does_Not_Run_Later_Stages_And_Can_Be_Retried()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddDeployment();
        var fail = true; var completed = false;
        builder.Services.AddSingleton(new ApplicationInitializationStep("schema", 10, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (fail) throw new InvalidOperationException("schema failure");
            return Task.CompletedTask;
        }));
        builder.Services.AddSingleton(new ApplicationInitializationStep("runtime", 20, (_, _) =>
        {
            completed = true; return Task.CompletedTask;
        }));
        using var host = builder.Build();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => host.InitializeApplicationAsync(cancelled.Token));
        await Should.ThrowAsync<InvalidOperationException>(() => host.InitializeApplicationAsync());
        completed.ShouldBeFalse();
        fail = false;
        await host.InitializeApplicationAsync();
        completed.ShouldBeTrue();
    }
}
