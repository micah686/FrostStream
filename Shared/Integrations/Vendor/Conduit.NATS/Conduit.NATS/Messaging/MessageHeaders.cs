namespace Conduit.NATS;

/// <summary>
/// Immutable bag of string message headers carried alongside a NATS message.
/// </summary>
public record MessageHeaders
{
    public Dictionary<string, string> Headers { get; }

    public MessageHeaders(Dictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var kvp in headers)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
                throw new ArgumentException("Header key cannot be null or whitespace", nameof(headers));
            if (kvp.Value == null)
                throw new ArgumentException($"Header value for key '{kvp.Key}' cannot be null", nameof(headers));
        }
        Headers = headers;
    }

    public static MessageHeaders Empty => new(new Dictionary<string, string>());
}
