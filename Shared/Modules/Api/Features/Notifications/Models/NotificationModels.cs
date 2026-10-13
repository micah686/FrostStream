namespace WebAPI.Features.Notifications.Models;

public sealed record NotificationSecretsUpsertRequest
{
    public IReadOnlyDictionary<string, string> Secrets { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
