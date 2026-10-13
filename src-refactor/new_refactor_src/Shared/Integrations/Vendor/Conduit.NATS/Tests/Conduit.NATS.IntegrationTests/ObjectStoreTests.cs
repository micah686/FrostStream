using System.Text;
using Conduit.NATS.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.IntegrationTests;

[ClassDataSource<NatsFixture>(Shared = SharedType.PerTestSession)]
public sealed class ObjectStoreTests
{
    private readonly NatsFixture _nats;

    public ObjectStoreTests(NatsFixture nats) => _nats = nats;

    [Test]
    [Timeout(60_000)]
    public async Task Put_Get_Delete_RoundTrips_And_Delete_Is_Idempotent(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();

        await sp.GetRequiredService<ITopologyManager>()
            .EnsureObjectStoreAsync(new ObjectStoreSpec { Name = BucketName.From("manifests") }, cancellationToken);

        var store = sp.GetRequiredService<Func<string, IObjectStore>>()("manifests");
        var payload = Encoding.UTF8.GetBytes("local-import-manifest-contents");

        var storedName = await store.PutAsync("import-42.json", new MemoryStream(payload), cancellationToken);
        storedName.ShouldBe("import-42.json");

        using var target = new MemoryStream();
        await store.GetAsync("import-42.json", target, cancellationToken);
        target.ToArray().ShouldBe(payload);

        await store.DeleteAsync("import-42.json", cancellationToken);
        // Second delete must be a no-op, not a throw.
        await store.DeleteAsync("import-42.json", cancellationToken);

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var afterDelete = new MemoryStream();
            await store.GetAsync("import-42.json", afterDelete, cancellationToken);
        });
    }

    [Test]
    [Timeout(60_000)]
    public async Task Factory_Returns_Same_Instance_Per_Bucket(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var factory = sp.GetRequiredService<Func<string, IObjectStore>>();

        factory("cache").ShouldBeSameAs(factory("cache"));
        factory("cache").ShouldNotBeSameAs(factory("other"));
    }
}
