using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using System.Text.Json;
using TUnit.Core;
using UnitTests.Storage;

namespace UnitTests.DataBridge;

public sealed class NotificationPreferencesConsumerServiceTests
{
    [Test]
    public async Task Upsert_Provider_Stores_Notification_Profile_In_User_Preferences()
    {
        var bus = new FakeMessageBus();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), bus);
        await SeedUserAsync(services, "user-1");
        var service = BuildService(bus, services);
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus, 5);

        var provider = new NotificationProviderDto
        {
            ProviderKey = "discord-main",
            ProviderKind = "discord",
            DisplayName = "Main Discord",
            NotifyConfig = JsonDocument.Parse("""{ "webhookUrl": "secret://discord-main/webhook" }""").RootElement.Clone()
        };

        var upsert = await bus.InvokeAsync<NotificationUpsertProviderRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.UpsertProvider,
            new NotificationUpsertProviderRequestMessage { OwnerSubject = "user-1", Provider = provider });

        upsert!.Success.ShouldBeTrue();
        upsert.Preferences!.Providers.ShouldHaveSingleItem().ProviderKey.ShouldBe("discord-main");

        var get = await bus.InvokeAsync<NotificationGetPreferencesRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.GetPreferences,
            new NotificationGetPreferencesRequestMessage { OwnerSubject = "user-1" });

        get!.Success.ShouldBeTrue();
        get.Preferences!.Providers.ShouldHaveSingleItem().DisplayName.ShouldBe("Main Discord");

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Delete_Provider_Removes_It_From_Rules()
    {
        var bus = new FakeMessageBus();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), bus);
        await SeedUserAsync(services, "user-1");
        var service = BuildService(bus, services);
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus, 5);

        var preferences = new NotificationPreferencesDto
        {
            Enabled = true,
            Providers =
            [
                new NotificationProviderDto
                {
                    ProviderKey = "slack-main",
                    ProviderKind = "slack",
                    NotifyConfig = JsonDocument.Parse("""{ "webhookUrl": "secret://slack-main/webhook" }""").RootElement.Clone()
                }
            ],
            Rules =
            [
                new NotificationRuleDto
                {
                    EventKey = NotificationEventKeys.DownloadCompleted,
                    ProviderKeys = ["slack-main"]
                }
            ]
        };

        await bus.InvokeAsync<NotificationUpdatePreferencesRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.UpdatePreferences,
            new NotificationUpdatePreferencesRequestMessage { OwnerSubject = "user-1", Preferences = preferences });

        var deleted = await bus.InvokeAsync<NotificationDeleteProviderRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.DeleteProvider,
            new NotificationDeleteProviderRequestMessage { OwnerSubject = "user-1", ProviderKey = "slack-main" });

        deleted!.Success.ShouldBeTrue();
        deleted.Preferences!.Providers.ShouldBeEmpty();
        deleted.Preferences.Rules.ShouldHaveSingleItem().ProviderKeys.ShouldBeEmpty();

        await service.StopAsync(CancellationToken.None);
    }

    private static NotificationPreferencesConsumerService BuildService(FakeMessageBus bus, ServiceProvider services)
        => new(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            new FakeNotificationDispatcher(),
            new FixedClock(DataBridgeTestHelpers.Now),
            NullLogger<NotificationPreferencesConsumerService>.Instance);

    private static async Task SeedUserAsync(ServiceProvider services, string subject)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        db.FrostStreamUsers.Add(new FrostStreamUserEntity
        {
            Id = Guid.NewGuid(),
            AuthentikSubjectId = subject,
            DisplayName = subject,
            CreatedAt = DataBridgeTestHelpers.Now
        });
        await db.SaveChangesAsync();
    }

    private static async Task WaitForSubscriptionsAsync(FakeMessageBus bus, int expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (bus.Subscriptions.Count == expected)
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Consumer did not register subscriptions in time.");
    }

    private sealed class FakeNotificationDispatcher : INotificationDispatcher
    {
        public Task<NotificationDispatchResult> SendTestAsync(NotificationTestRequestMessage request, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationDispatchResult(true));

        public Task NotifyDownloadOutcomeAsync(Guid jobId, string eventKey, string subject, string body, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyScheduleFailureAsync(string scheduleKey, string failureMessage, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
