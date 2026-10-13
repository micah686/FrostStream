using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using Microsoft.Extensions.Options;
using Shared.LiveChat;

namespace DataBridge.LiveChat;

/// <summary>
/// Owns the ClickHouse client for the optional live-chat store: connections for queries/DDL and
/// the binary-insert path for batched ingest. Registered in DI only when
/// <see cref="LiveChatOptions.Enabled"/> — nothing else in DataBridge may assume it resolves.
/// </summary>
public sealed class ClickHouseAccess : IDisposable
{
    private readonly ClickHouseClient _client;

    public ClickHouseAccess(IOptions<LiveChatOptions> options)
    {
        var value = options.Value;
        Database = value.Database;
        _client = new ClickHouseClient(BuildConnectionString(value));
    }

    public string Database { get; }

    public ClickHouseClient Client => _client;

    public ClickHouseConnection CreateConnection() => _client.CreateConnection();

    /// <summary>Fully-qualified table name for insert targets.</summary>
    public string Table(string name) => $"{Database}.{name}";

    public void Dispose() => _client.Dispose();

    private static string BuildConnectionString(LiveChatOptions options)
    {
        var uri = new Uri(options.Url, UriKind.Absolute);
        return $"Host={uri.Host};Port={uri.Port};Protocol={uri.Scheme};" +
               $"Username={options.User};Password={options.Password};Database={options.Database}";
    }
}
