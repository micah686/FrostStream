using DataBridge.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using UnitTests.Storage;

namespace UnitTests.DataBridge;

/// <summary>
/// Cookie profile metadata is owner-scoped by the validated subject WebAPI supplies, so one user can
/// never see or mutate another user's profiles.
/// </summary>
public sealed class CookieProfileConsumerServiceTests
{
    private const string OwnerA = "auth0|alice";
    private const string OwnerB = "auth0|bob";

    [Test]
    public async Task One_Owner_Cannot_Read_Or_Delete_Another_Owners_Profile()
    {
        var bus = new FakeMessageBus();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), bus);
        var service = BuildService(bus, services);
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus, 4);

        // Owner A creates a profile.
        var created = await bus.InvokeAsync<CookieProfileUpsertRequestMessage, CookieProfileOperationResponseMessage>(
            CookieProfileSubjects.Upsert,
            new CookieProfileUpsertRequestMessage { OwnerSubject = OwnerA, ProfileKey = "yt", Site = "youtube.com" });
        created!.Success.ShouldBeTrue();

        // Owner B cannot get it.
        var bGet = await bus.InvokeAsync<CookieProfileGetRequestMessage, CookieProfileOperationResponseMessage>(
            CookieProfileSubjects.Get,
            new CookieProfileGetRequestMessage { OwnerSubject = OwnerB, ProfileKey = "yt" });
        bGet!.Success.ShouldBeFalse();
        bGet.ErrorCode.ShouldBe("not_found");

        // Owner B's list is empty.
        var bList = await bus.InvokeAsync<CookieProfileListRequestMessage, CookieProfileOperationResponseMessage>(
            CookieProfileSubjects.List,
            new CookieProfileListRequestMessage { OwnerSubject = OwnerB });
        bList!.Success.ShouldBeTrue();
        bList.Items.ShouldBeEmpty();

        // Owner B cannot delete it.
        var bDelete = await bus.InvokeAsync<CookieProfileDeleteRequestMessage, CookieProfileOperationResponseMessage>(
            CookieProfileSubjects.Delete,
            new CookieProfileDeleteRequestMessage { OwnerSubject = OwnerB, ProfileKey = "yt" });
        bDelete!.Success.ShouldBeFalse();
        bDelete.ErrorCode.ShouldBe("not_found");

        // Owner A still has it.
        var aGet = await bus.InvokeAsync<CookieProfileGetRequestMessage, CookieProfileOperationResponseMessage>(
            CookieProfileSubjects.Get,
            new CookieProfileGetRequestMessage { OwnerSubject = OwnerA, ProfileKey = "yt" });
        aGet!.Success.ShouldBeTrue();
        aGet.Entity!.OwnerSubject.ShouldBe(OwnerA);
        aGet.Entity.Site.ShouldBe("youtube.com");

        await service.StopAsync(CancellationToken.None);
    }

    private static CookieProfileConsumerService BuildService(FakeMessageBus bus, ServiceProvider services)
        => new(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(DataBridgeTestHelpers.Now),
            NullLogger<CookieProfileConsumerService>.Instance);

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
