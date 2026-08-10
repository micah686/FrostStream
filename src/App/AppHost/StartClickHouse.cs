namespace AppHost;

public sealed record ClickHouseResources(
    IResourceBuilder<ContainerResource>? Server,
    EndpointReference? HttpEndpoint,
    IResourceBuilder<ParameterResource>? Password);

// ClickHouse backs the optional live-chat replay feature (LIVE_CHAT_ENABLED). Chat data is a
// derived projection of the live_chat.json sidecars in blob storage — the volume can be wiped
// and rebuilt via the backfill job.
public static class StartClickHouse
{
    public const string Database = "froststream";
    public const string User = "froststream";

    public static ClickHouseResources Start(IDistributedApplicationBuilder builder)
    {
        if (!Helpers.LiveChatEnabled)
        {
            return new ClickHouseResources(Server: null, HttpEndpoint: null, Password: null);
        }

        var password = builder.AddParameter(
            "clickhouse-password",
            Helpers.GetEnv("CLICKHOUSE_PASSWORD"),
            publishValueAsDefault: false,
            secret: true);

        var server = builder
            .AddContainer("clickhouse", "clickhouse/clickhouse-server",
                Environment.GetEnvironmentVariable("CLICKHOUSE_IMAGE_TAG") ?? "25.8")
            .WithVolume("clickhouse-data", "/var/lib/clickhouse")
            .WithEnvironment("CLICKHOUSE_DB", Database)
            .WithEnvironment("CLICKHOUSE_USER", User)
            .WithEnvironment("CLICKHOUSE_PASSWORD", password)
            // Internal-only: the compose export keeps this off the host network.
            .WithHttpEndpoint(port: Ports.ClickHouse, targetPort: 8123, name: "http");

        return new ClickHouseResources(server, server.GetEndpoint("http"), password);
    }
}
