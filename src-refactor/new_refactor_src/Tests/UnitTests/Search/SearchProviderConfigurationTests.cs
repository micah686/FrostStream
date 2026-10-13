using FrostStream.Integrations.Typesense;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

public sealed class SearchProviderConfigurationTests
{
    [Test]
    [Arguments("ConnectionStrings:typesense")]
    [Arguments("Typesense:Host")]
    [Arguments("Typesense:Port")]
    [Arguments("Typesense:Protocol")]
    public void Full_Rejects_Obsolete_Provider_Aliases(string alias)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Typesense:Url"] = "http://localhost:8108";
        builder.Configuration["Typesense:ApiKey"] = "test-key";
        builder.Configuration[alias] = "obsolete";
        Should.Throw<InvalidOperationException>(() => builder.AddTypesenseIntegration()).Message.ShouldContain(alias);
    }

    [Test]
    [Arguments(null, "key")]
    [Arguments("file:///tmp/search", "key")]
    [Arguments("http://localhost:8108", null)]
    public void Full_Requires_Explicit_Http_Endpoint_And_Key(string? url, string? key)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Typesense:Url"] = url;
        builder.Configuration["Typesense:ApiKey"] = key;
        Should.Throw<InvalidOperationException>(() => builder.AddTypesenseIntegration());
    }
}
