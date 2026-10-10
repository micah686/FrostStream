using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using UnitTests.Storage;

namespace UnitTests.DataBridge;

public sealed class UserSessionConsumerServiceTests
{
    [Test]
    public async Task Upsert_Creates_Then_Updates_The_Same_Row_Without_Duplicates()
    {
        var bus = new FakeMessageBus();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), bus);
        var service = BuildService(bus, services);
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus, 1);

        var first = await bus.InvokeAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(
            UserSessionSubjects.Upsert,
            new UserSessionUpsertRequestMessage { Subject = "auth0|abc", DisplayName = "Micah" });
        first!.Success.ShouldBeTrue();

        var second = await bus.InvokeAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(
            UserSessionSubjects.Upsert,
            new UserSessionUpsertRequestMessage { Subject = "auth0|abc", DisplayName = "Micah Renamed" });
        second!.Success.ShouldBeTrue();

        // Same logical user => same id, single row.
        second.UserId.ShouldBe(first.UserId);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var rows = await db.FrostStreamUsers.Where(x => x.AuthentikSubjectId == "auth0|abc").ToListAsync();
        rows.ShouldHaveSingleItem();
        rows[0].DisplayName.ShouldBe("Micah Renamed");
        rows[0].LastUpdated.ShouldNotBeNull();

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Upsert_Rejects_A_Blank_Subject()
    {
        var bus = new FakeMessageBus();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), bus);
        var service = BuildService(bus, services);
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus, 1);

        var response = await bus.InvokeAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(
            UserSessionSubjects.Upsert,
            new UserSessionUpsertRequestMessage { Subject = "  ", DisplayName = "x" });

        response!.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("validation");

        await service.StopAsync(CancellationToken.None);
    }

    private static UserSessionConsumerService BuildService(FakeMessageBus bus, ServiceProvider services)
        => new(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(DataBridgeTestHelpers.Now),
            NullLogger<UserSessionConsumerService>.Instance);

    private static async Task WaitForSubscriptionsAsync(FakeMessageBus bus, int expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (bus.Subscriptions.Count == expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Consumer did not register subscriptions in time.");
    }
}
