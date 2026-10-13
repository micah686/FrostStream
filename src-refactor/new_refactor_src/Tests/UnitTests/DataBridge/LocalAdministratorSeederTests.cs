using FrostStream.Lite.Infrastructure;
using FrostStream.Full.Infrastructure;
using DataBridge.Data;
using FrostStream.Lite.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using UnitTests.Storage;

namespace UnitTests.DataBridge;

public sealed class LocalAdministratorSeederTests
{
    [Test]
    public async Task Lite_Seeds_Admin_Before_Start_Returns_And_Reuses_Identity_After_Restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            for (var restart = 0; restart < 2; restart++)
            {
                var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
                builder.Configuration["Deployment:Mode"] = "Lite";
                builder.Configuration["Auth:SingleUserMode"] = "false";
                builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(dir, "test.db");
                builder.AddLitePersistence().AddLiteMessaging().AddLiteSecretStore().AddLiteBackupClient().AddLiteStagedStorage();
                using var host = builder.Build();
                global::DataBridge.DataBridgeModule.InitializeDataBridge(host);
                var seeder = new LocalAdministratorSeeder(
                    host.Services.GetRequiredService<IServiceScopeFactory>(),
                    new FixedClock(DataBridgeTestHelpers.Now), NullLogger<LocalAdministratorSeeder>.Instance);
                await seeder.InitializeAsync(default);
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
                var rows = await db.FrostStreamUsers.ToListAsync();
                rows.ShouldHaveSingleItem();
                rows[0].Id.ShouldBe(AuthConstants.SingleUserId);
                rows[0].AuthentikSubjectId.ShouldBe(AuthConstants.SingleUserSubject);
                rows[0].DisplayName.ShouldBe("Admin");
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Seeds_Then_Reuses_The_Stable_Owner_Row()
    {
        var dbName = Guid.NewGuid().ToString("n");

        await using var services = StorageTestHelpers.BuildDbServices(dbName, new InMemorySecretStore(), new FakeMessageBus());

        await RunSeederAsync(services);
        await AssertSingleOwnerRowAsync(services);

        // A second run (e.g. restart) must not create a duplicate.
        await RunSeederAsync(services);
        await AssertSingleOwnerRowAsync(services);
    }

    private static async Task RunSeederAsync(ServiceProvider services)
    {
        var seeder = new LocalAdministratorSeeder(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(DataBridgeTestHelpers.Now),
            NullLogger<LocalAdministratorSeeder>.Instance);
        await seeder.InitializeAsync(CancellationToken.None);
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
