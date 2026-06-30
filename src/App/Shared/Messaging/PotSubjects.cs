namespace Shared.Messaging;

/// <summary>
/// NATS subjects for the Proof-of-Origin Token (POT) tunnel. A Worker's loopback HTTP→NATS shim
/// issues request/reply messages on <see cref="Request"/>; POT brokers answer them from the
/// <see cref="BrokersQueueGroup"/> queue group, replaying the tunneled HTTP call against a nearby
/// bgutil-ytdlp-pot-provider container.
/// </summary>
public static class PotSubjects
{
    /// <summary>Request/reply subject carrying a tunneled bgutil HTTP call.</summary>
    public const string Request = "pot.request";

    /// <summary>Queue group across all brokers so a request is handled by exactly one healthy broker.</summary>
    public const string BrokersQueueGroup = "pot-brokers";
}
