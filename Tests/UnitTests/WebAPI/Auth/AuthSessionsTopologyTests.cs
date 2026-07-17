using Conduit.NATS;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.WebAPI.Auth;

public sealed class AuthSessionsTopologyTests
{
    [Test]
    public void Browser_Sessions_Use_A_Bounded_File_Backed_Kv_Bucket()
    {
        var bucket = new AuthSessionsTopology().GetBuckets().ShouldHaveSingleItem();

        bucket.Name.Value.ShouldBe(AuthSessionsTopology.BucketNameValue);
        bucket.StorageType.ShouldBe(StorageType.File);
        bucket.History.ShouldBe(1);
        bucket.MaxAge.ShouldBe(TimeSpan.FromDays(31));
        bucket.MaxBytes.ShouldBeGreaterThan(0);
    }
}
