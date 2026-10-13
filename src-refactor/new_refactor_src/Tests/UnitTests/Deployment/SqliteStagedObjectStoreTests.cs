using DataBridge.Persistence;
using DataBridge.Persistence.Sqlite;
using NodaTime;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class SqliteStagedObjectStoreTests
{
    [Test]
    public async Task Manifests_Survive_Restart_Overwrite_And_Delete_With_Bucket_Isolation()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.db");
        SqliteConnectionFactory Factory() => new(new(PersistenceProvider.Sqlite, path, 5));
        try
        {
            new SqliteSchemaInitializer(Factory(), SystemClock.Instance).Initialize();
            var store = new SqliteStagedObjectStore(Factory(), "manifests");
            (await store.PutAsync("session.json", new MemoryStream([1, 2, 3]))).ShouldBe("session.json");
            var other = new SqliteStagedObjectStore(Factory(), "other");
            await other.PutAsync("session.json", new MemoryStream([9]));
            await store.DisposeAsync();
            var restarted = new SqliteStagedObjectStore(Factory(), "manifests");
            using var target = new MemoryStream();
            await restarted.GetAsync("session.json", target);
            target.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            await restarted.PutAsync("session.json", new MemoryStream([4]));
            target.SetLength(0);
            await restarted.GetAsync("session.json", target);
            target.ToArray().ShouldBe(new byte[] { 4 });
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => restarted.PutAsync("session.json", new MemoryStream([5]), cancel.Token));
            await restarted.DeleteAsync("session.json");
            await restarted.DeleteAsync("session.json");
            await Should.ThrowAsync<KeyNotFoundException>(() => restarted.GetAsync("session.json", target));
            target.SetLength(0);
            await other.GetAsync("session.json", target);
            target.ToArray().ShouldBe(new byte[] { 9 });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
