using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace UnitTests.DataBridge;

internal static class DataBridgeTestHelpers
{
    public static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 21, 0);

    public static DataBridgeDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DataBridgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new DataBridgeDbContext(options);
    }
}

internal sealed class FixedClock(Instant now) : IClock
{
    public Instant GetCurrentInstant() => now;
}
