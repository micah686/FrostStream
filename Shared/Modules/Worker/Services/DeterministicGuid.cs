using System.IO.Hashing;
using System.Text;

namespace Worker.Services;

/// <summary>
/// Builds a Guid deterministically from a base Guid and a string suffix using XxHash128.
/// Used so that JetStream redeliveries of the same command produce result events with stable
/// MessageIds, enabling server-side dedupe (Nats-Msg-Id) and DataBridge processed-messages dedupe.
/// </summary>
internal static class DeterministicGuid
{
    public static Guid Create(Guid seed, string suffix)
    {
        Span<byte> input = stackalloc byte[16 + Encoding.UTF8.GetByteCount(suffix)];
        seed.TryWriteBytes(input);
        Encoding.UTF8.GetBytes(suffix, input[16..]);

        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(input, hash);
        return new Guid(hash);
    }
}
