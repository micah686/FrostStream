using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.NATS;
using NodaTime;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

public enum Tier { Free, Premium }

public record Account(string Name, Instant CreatedAt, Tier Tier, string? Note = null);

public record Plain(string Name, int Count);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Account))]
public partial class AccountJsonContext : JsonSerializerContext;

public sealed class JsonSerializerRegistryTests
{
    private static string Serialize<T>(JsonSerializerRegistry registry, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        registry.GetSerializer<T>().Serialize(buffer, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static T? Deserialize<T>(JsonSerializerRegistry registry, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return registry.GetDeserializer<T>().Deserialize(new ReadOnlySequence<byte>(bytes));
    }

    [Test]
    public async Task Default_Options_Use_CamelCase_StringEnum_NodaTime_And_OmitNull()
    {
        var registry = new JsonSerializerRegistry();
        var account = new Account("Ada", Instant.FromUtc(2024, 1, 2, 3, 4), Tier.Premium);

        var json = Serialize(registry, account);

        json.ShouldContain("\"name\":\"Ada\"");
        json.ShouldContain("\"createdAt\":\"2024-01-02T03:04:00Z\"");
        json.ShouldContain("\"tier\":\"Premium\"");
        json.ShouldNotContain("note"); // null omitted

        var round = Deserialize<Account>(registry, json);
        round.ShouldBe(account);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SourceGen_Context_Drives_Metadata_And_RoundTrips()
    {
        var options = JsonSerializerRegistry.CreateDefaultOptions(AccountJsonContext.Default);
        var registry = new JsonSerializerRegistry(options);

        // The supplied source-gen context actually resolves the type.
        AccountJsonContext.Default.GetTypeInfo(typeof(Account)).ShouldNotBeNull();

        var account = new Account("Grace", Instant.FromUtc(1999, 12, 31, 23, 59), Tier.Free);
        var json = Serialize(registry, account);

        json.ShouldContain("\"name\":\"Grace\"");
        json.ShouldContain("\"tier\":\"Free\"");
        Deserialize<Account>(registry, json).ShouldBe(account);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Custom_Options_Override_Is_Used_Verbatim()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = null };
        var registry = new JsonSerializerRegistry(options);

        var json = Serialize(registry, new Plain("Pas", 7));
        json.ShouldContain("\"Name\":\"Pas\""); // PascalCase preserved (no camelCase policy)
        await Task.CompletedTask;
    }
}
