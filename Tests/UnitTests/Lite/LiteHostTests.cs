using System.Text.Json;
using DataBridge.Lite;
using FrostStream.Lite;
using FrostStream.Lite.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shared.Application;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Lite;

public sealed class LiteHostTests
{
    [Test]
    public void Command_Dispatch_Recognizes_Reserved_Modes_And_Rejects_Unknown_Mode()
    {
        LiteCommandLine.Parse([]).ShouldBe(LiteCommand.Run);
        LiteCommandLine.Parse(["--urls", "http://127.0.0.1:5098"]).ShouldBe(LiteCommand.Run);
        LiteCommandLine.Parse(["run"]).ShouldBe(LiteCommand.Run);
        LiteCommandLine.Parse(["initialize"]).ShouldBe(LiteCommand.Initialize);
        LiteCommandLine.Parse(["backup"]).ShouldBe(LiteCommand.Backup);
        LiteCommandLine.Parse(["restore"]).ShouldBe(LiteCommand.Restore);
        Should.Throw<ArgumentException>(() => LiteCommandLine.Parse(["unexpected"]));
    }

    [Test]
    public void Production_Validation_Requires_Data_Settings_But_Not_Identity_Or_Broker_Settings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:froststreamdb"] = "Host=postgres;Database=froststreamdb;Username=froststream;Password=synthetic",
            ["Typesense:Url"] = "http://typesense:8108",
            ["Typesense:ApiKey"] = "synthetic",
            ["FROSTSTREAM_STORAGE_ROOT"] = "/data"
        }).Build();

        LiteProductionValidation.Validate(configuration, new TestHostEnvironment("Production"));

        var empty = new ConfigurationBuilder().Build();
        var exception = Should.Throw<InvalidOperationException>(() =>
            LiteProductionValidation.Validate(empty, new TestHostEnvironment("Production")));
        exception.Message.ShouldContain("ConnectionStrings:froststreamdb");
        exception.Message.ShouldNotContain("Auth");
        exception.Message.ShouldNotContain("NATS");
        exception.Message.ShouldNotContain("OpenFga");
        exception.Message.ShouldNotContain("OpenBao");
    }

    [Test]
    public void Lite_Api_Module_Registers_Direct_Operations_Without_Transport_Or_Authentication()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:froststreamdb"] = "Host=localhost;Database=froststreamdb;Username=postgres;Password=synthetic"
        }).Build();
        var services = new ServiceCollection();

        services.AddSingleton<ICurrentOwner, FixedCurrentOwner>();
        services.AddDataBridgeLiteApiOperations(configuration);
        services.AddLiteDurableExecution(configuration);
        services.AddLiteAcquisition(configuration);
        services.AddControllers().AddApplicationPart(typeof(SystemController).Assembly);

        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IUserNoteApplication));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(ILiteExecutionLease));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(ILocalExecutionStore));
        services.Count(descriptor => descriptor.ServiceType == typeof(ILocalExecutionHandler)).ShouldBe(4);
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(ILiteDownloadIngress));
        services.ShouldNotContain(descriptor => ContainsForbiddenName(descriptor.ServiceType));
        services.Any(descriptor => descriptor.ImplementationType != null &&
                                   ContainsForbiddenName(descriptor.ImplementationType)).ShouldBeFalse();
    }

    [Test]
    public void Compatibility_Session_Is_Sessionless_And_Stable_Across_Clients()
    {
        var first = new SystemController(new FixedCurrentOwner());
        var second = new SystemController(new FixedCurrentOwner());

        var firstJson = SerializeResult(first.GetCurrentOwner());
        var secondJson = SerializeResult(second.GetCurrentOwner());

        firstJson.RootElement.GetProperty("authenticated").GetBoolean().ShouldBeFalse();
        firstJson.RootElement.GetProperty("profile").GetProperty("subject").GetString()
            .ShouldBe(AuthConstants.SingleUserSubject);
        secondJson.RootElement.GetProperty("profile").GetProperty("subject").GetString()
            .ShouldBe(AuthConstants.SingleUserSubject);
    }

    [Test]
    public void Composition_Rejects_Duplicate_Hosted_Service_Registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService, DuplicateHostedService>();
        services.AddSingleton<IHostedService, DuplicateHostedService>();

        Should.Throw<InvalidOperationException>(() => LiteCompositionValidator.ValidateServices(services))
            .Message.ShouldContain(nameof(DuplicateHostedService));
    }

    [Test]
    public void Composition_Rejects_Route_Collisions()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        var duplicateRoute = string.Concat("/", "duplicate");
        app.MapGet(duplicateRoute, () => "first");
        app.MapGet(duplicateRoute, () => "second");

        Should.Throw<InvalidOperationException>(() => LiteCompositionValidator.ValidateRoutes(app))
            .Message.ShouldContain("GET /duplicate");
    }

    [Test]
    public async Task User_Notes_Always_Pass_The_Fixed_Owner_To_Direct_Application()
    {
        var application = Substitute.For<IUserNoteApplication>();
        UserNoteSearchRequestMessage? captured = null;
        application.SearchAsync(Arg.Do<UserNoteSearchRequestMessage>(request => captured = request), Arg.Any<CancellationToken>())
            .Returns(new UserNoteSearchResponseMessage { Success = true, Items = [] });
        var controller = new UserNotesController(application, new FixedCurrentOwner());

        var result = await controller.List(cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        captured.ShouldNotBeNull();
        captured.OwnerSubject.ShouldBe(AuthConstants.SingleUserSubject);
    }

    private static JsonDocument SerializeResult(IActionResult result)
    {
        var value = result.ShouldBeOfType<OkObjectResult>().Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(value));
    }

    private static bool ContainsForbiddenName(Type type)
        => type.FullName?.Contains("Conduit.NATS", StringComparison.OrdinalIgnoreCase) == true
           || type.FullName?.Contains("OpenFga", StringComparison.OrdinalIgnoreCase) == true
           || type.FullName?.Contains("OpenBao", StringComparison.OrdinalIgnoreCase) == true
           || type.FullName?.StartsWith("WebAPI.Auth", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "FrostStream.Lite.Tests";
        public string ContentRootPath { get; set; } = "/tmp";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class DuplicateHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
