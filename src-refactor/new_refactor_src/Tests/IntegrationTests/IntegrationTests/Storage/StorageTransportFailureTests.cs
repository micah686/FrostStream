using IntegrationTests.Infrastructure;
using Shouldly;
using Shared.Messaging;
using System.Diagnostics;
using System.Net;
using TUnit.Core;

namespace IntegrationTests.Storage;

public class StorageTransportFailureTests
{
    private static readonly StorageStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static StorageTransportFailureTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task ResetAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        await Fixture.ResetAsync();
        await Fixture.StartDataBridgeAsync();
    }

    [After(Test)]
    public void Release()
    {
        Gate.Release();
    }

    [Test]
    public async Task Stopped_DataBridge_Returns_503_Within_Request_Timeout()
    {
        await Fixture.StopDataBridgeAsync();

        var stopwatch = Stopwatch.StartNew();
        var response = await Fixture.Client.GetAsync("/api/storage/list");
        stopwatch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(12));
    }

    [Test]
    public async Task Slow_Handler_Exceeding_Timeout_Returns_503()
    {
        await Fixture.StopDataBridgeAsync();
        await using var harness = await Fixture.CreateExternalBusAsync();
        await harness.Bus.SubscribeAsync<StorageListRequestMessage>(
            StorageSubjects.ListStorage,
            async ctx =>
            {
                await Task.Delay(TimeSpan.FromSeconds(11));
                await ctx.RespondAsync(new StorageOperationResponseMessage { Success = true, Items = [] });
            },
            queueGroup: "databridge-storage");

        var response = await Fixture.Client.GetAsync("/api/storage/list");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
