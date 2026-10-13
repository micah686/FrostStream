using NATS.Client.Core;

namespace Conduit.NATS;

/// <summary>Maps between the library's <see cref="MessageHeaders"/> and the client's <see cref="NatsHeaders"/>.</summary>
internal static class NatsHeaderConverter
{
    public static NatsHeaders? ToNats(MessageHeaders? headers)
    {
        if (headers is null || headers.Headers.Count == 0)
            return null;

        var natsHeaders = new NatsHeaders();
        foreach (var kvp in headers.Headers)
            natsHeaders[kvp.Key] = kvp.Value;
        return natsHeaders;
    }

    public static MessageHeaders FromNats(NatsHeaders? headers)
    {
        if (headers is null || headers.Count == 0)
            return MessageHeaders.Empty;

        return new MessageHeaders(headers.ToDictionary(k => k.Key, k => k.Value.ToString()));
    }
}
