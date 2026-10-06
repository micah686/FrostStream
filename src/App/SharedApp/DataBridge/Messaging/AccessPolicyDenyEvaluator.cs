using Shared.Messaging;

namespace DataBridge.Messaging;

internal sealed record AccessPolicyDenyScope(
    Guid PolicyId,
    IReadOnlyCollection<Guid> MediaGuids,
    IReadOnlyCollection<string> Providers,
    IReadOnlyCollection<int> MinimumAges);

internal static class AccessPolicyDenyEvaluator
{
    public static AccessPolicyEffectiveMediaDto Evaluate(
        AccessPolicyMediaSummaryDto media,
        IReadOnlyCollection<AccessPolicyDenyScope> assignedScopes,
        bool bypass)
    {
        if (!media.Found)
        {
            return new AccessPolicyEffectiveMediaDto
            {
                MediaGuid = media.MediaGuid,
                Found = false,
                IsAllowed = false,
                Decisions = []
            };
        }

        if (bypass)
        {
            return Result(media, true,
            [
                new AccessPolicyAxisDecisionDto
                {
                    Axis = "bypass",
                    Restricted = false,
                    Allowed = true,
                    Reason = "The principal belongs to an administrative bypass group."
                }
            ]);
        }

        var decisions = new List<AccessPolicyAxisDecisionDto>
        {
            Decision(
                "media",
                media.MediaGuid.ToString(),
                assignedScopes
                    .Where(scope => scope.MediaGuids.Contains(media.MediaGuid))
                    .Select(scope => scope.PolicyId),
                "An assigned policy denies this media GUID.",
                "No assigned policy denies this media GUID.")
        };

        var providers = media.Providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Select(provider => provider.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (providers.Length == 0)
        {
            decisions.Add(Decision(
                "provider",
                null,
                [],
                "",
                "The media has no provider to evaluate."));
        }
        else
        {
            foreach (var provider in providers)
            {
                decisions.Add(Decision(
                    "provider",
                    provider,
                    assignedScopes
                        .Where(scope => scope.Providers.Contains(provider, StringComparer.OrdinalIgnoreCase))
                        .Select(scope => scope.PolicyId),
                    $"An assigned policy denies provider '{provider}'.",
                    $"No assigned policy denies provider '{provider}'."));
            }
        }

        if (media.AgeLimit is { } ageLimit)
        {
            decisions.Add(Decision(
                "age",
                ageLimit.ToString(),
                assignedScopes
                    .Where(scope => scope.MinimumAges.Any(minimumAge => ageLimit >= minimumAge))
                    .Select(scope => scope.PolicyId),
                $"An assigned policy denies media rated {ageLimit}+.",
                $"No assigned policy denies media rated {ageLimit}+."));
        }
        else
        {
            decisions.Add(Decision(
                "age",
                "unrated",
                [],
                "",
                "Unrated media is not denied by age-tier policies."));
        }

        return Result(media, decisions.All(decision => decision.Allowed), decisions);
    }

    private static AccessPolicyAxisDecisionDto Decision(
        string axis,
        string? resource,
        IEnumerable<Guid> denyingPolicyIds,
        string deniedReason,
        string allowedReason)
    {
        var deniers = denyingPolicyIds.Distinct().Order().ToArray();
        return new AccessPolicyAxisDecisionDto
        {
            Axis = axis,
            Resource = resource,
            Restricted = deniers.Length > 0,
            Allowed = deniers.Length == 0,
            MatchingPolicyIds = deniers,
            DenyingPolicyIds = deniers,
            Reason = deniers.Length > 0 ? deniedReason : allowedReason
        };
    }

    private static AccessPolicyEffectiveMediaDto Result(
        AccessPolicyMediaSummaryDto media,
        bool isAllowed,
        IReadOnlyList<AccessPolicyAxisDecisionDto> decisions)
        => new()
        {
            MediaGuid = media.MediaGuid,
            Found = true,
            Title = media.Title,
            Providers = media.Providers,
            AgeLimit = media.AgeLimit,
            IsAllowed = isAllowed,
            Decisions = decisions
        };
}
