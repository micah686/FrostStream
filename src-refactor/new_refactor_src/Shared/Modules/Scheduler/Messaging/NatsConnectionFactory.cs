using Microsoft.Extensions.Configuration;
using NATS.Client.Core;

namespace Scheduler.Messaging;

internal static class NatsConnectionFactory
{
    public static string GetUrl(IConfiguration configuration)
        => configuration.GetConnectionString("nats")
           ?? configuration["NATS:Url"]
           ?? "nats://localhost:24040";

    public static NatsAuthOpts? BuildAuth(IConfiguration configuration)
    {
        var token = configuration["NATS:Token"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            return new NatsAuthOpts { Token = token };
        }

        var username = configuration["NATS:Username"];
        var password = configuration["NATS:Password"];
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return new NatsAuthOpts
            {
                Username = username,
                Password = password
            };
        }

        var credsFile = configuration["NATS:CredsFile"];
        return string.IsNullOrWhiteSpace(credsFile) ? null : new NatsAuthOpts { CredsFile = credsFile };
    }
}
