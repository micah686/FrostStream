using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using UnitTests.Storage;

namespace UnitTests.DataBridge;

public sealed class SingleUserOwnerSeederServiceTests
{
    [Test]
    public async Task Seeds_Then_Reuses_The_Stable_Owner_Row()
    {
        var dbName = Guid.NewGuid().ToString("n");
        var config = SingleUserConfig(enabled: true);

        await using var services = StorageTestHelpers.BuildDbServices(dbName, new InMemorySecretStore(), new FakeMessageBus());

        await RunSeederAsync(services, config);
        await AssertSingleOwnerRowAsync(services);

        // A second run (e.g. restart) must not create a duplicate.
        await RunSeederAsync(services, config);
        await AssertSingleOwnerRowAsync(services);
    }

    [Test]
    public async Task Does_Nothing_Outside_Single_User_Mode()
    {
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), new FakeMessageBus());

        await RunSeederAsync(services, SingleUserConfig(enabled: false));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        (await db.FrostStreamUsers.CountAsync()).ShouldBe(0);
    }

    private static IConfiguration SingleUserConfig(bool enabled)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SINGLE_USER_MODE"] = enabled ? "true" : "false" })
            .Build();

    private static async Task RunSeederAsync(ServiceProvider services, IConfiguration config)
    {
        var seeder = new SingleUserOwnerSeederService(
            services.GetRequiredService<IServiceScopeFactory>(),
            config,
            new FixedClock(DataBridgeTestHelpers.Now),
            NullLogger<SingleUserOwnerSeederService>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        // The seeder runs to completion in ExecuteAsync; give the background task a moment to finish.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            if (await db.FrostStreamUsers.AnyAsync(x => x.Id == AuthConstants.SingleUserId))
            {
                break;
            }

            await Task.Delay(20);
        }

        await seeder.StopAsync(CancellationToken.None);
    }

    private static async Task AssertSingleOwnerRowAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var rows = await db.FrostStreamUsers.ToListAsync();
        rows.ShouldHaveSingleItem();
        rows[0].Id.ShouldBe(AuthConstants.SingleUserId);
        rows[0].AuthentikSubjectId.ShouldBe(AuthConstants.SingleUserSubject);
    }
}
