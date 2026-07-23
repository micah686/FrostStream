using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Management.Controllers;

[ApiController]
[Route("api/global/access-control")]
public sealed class AccessControlController(
    IMessageBus messageBus,
    IBundleManagementService bundles,
    IDirectoryService directory,
    IAccessPolicyOpenFgaService openFgaPolicies,
    ILogger<AccessControlController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpGet("directory")]
    [Endpoint(EndpointIds.AccessControlDirectorySearch)]
    [EndpointSummary("Search the identity provider directory")]
    [EndpointDescription("Searches Authentik for users or groups matching the query so policy assignments can use friendly names while preserving stable user subjects and group identifiers.")]
    public async Task<ActionResult<IReadOnlyList<DirectoryEntry>>> SearchDirectory(
        [FromQuery] string type,
        [FromQuery] string q,
        CancellationToken cancellationToken)
        => Ok(await directory.SearchAsync(type ?? "", q ?? "", cancellationToken));

    [HttpGet("catalog")]
    [Endpoint(EndpointIds.AccessControlCatalog)]
    [EndpointSummary("List the endpoint catalog")]
    [EndpointDescription("Returns every code-defined endpoint id and its seeded baseline bundle so custom bundles can only reference routes known to the running application.")]
    public ActionResult<IReadOnlyList<CatalogEntry>> GetCatalog() => Ok(bundles.GetCatalog());

    [HttpGet("bundles")]
    [Endpoint(EndpointIds.AccessControlBundlesList)]
    [EndpointSummary("List access-control bundles")]
    [EndpointDescription("Returns system and custom bundles with endpoint membership, endpoint counts, policy counts, and summaries of every access policy that references each bundle.")]
    public async Task<IActionResult> ListBundles(CancellationToken cancellationToken)
    {
        var bundleResult = await bundles.ListBundlesAsync(cancellationToken);
        if (bundleResult.Status != BundleOpStatus.Ok)
            return MapBundleError(new BundleOpResult(bundleResult.Status, bundleResult.Error));

        var policies = await ListPoliciesInternalAsync(cancellationToken);
        if (policies.Response is not null) return policies.Response;

        return Ok(bundleResult.Value!
            .Select(bundle => ToBundleView(bundle, policies.Policies!))
            .ToArray());
    }

    [HttpGet("bundles/{bundleId}")]
    [Endpoint(EndpointIds.AccessControlBundlesGet)]
    [EndpointSummary("Get an access-control bundle")]
    [EndpointDescription("Returns one system or custom bundle with its complete endpoint membership and summaries of the policies that currently reference the bundle.")]
    public async Task<IActionResult> GetBundle(string bundleId, CancellationToken cancellationToken)
    {
        var bundleResult = await bundles.GetBundleAsync(bundleId, cancellationToken);
        if (bundleResult.Status != BundleOpStatus.Ok)
            return MapBundleError(new BundleOpResult(bundleResult.Status, bundleResult.Error));

        var policies = await ListPoliciesInternalAsync(cancellationToken);
        if (policies.Response is not null) return policies.Response;
        return Ok(ToBundleView(bundleResult.Value!, policies.Policies!));
    }

    [HttpGet("bundles/{bundleId}/policies")]
    [Endpoint(EndpointIds.AccessControlBundlePoliciesList)]
    [EndpointSummary("List policies that reference a bundle")]
    [EndpointDescription("Returns the access-policy membership for one bundle; this is the same reference set used to prevent deletion of a bundle that remains in active policy configuration.")]
    public async Task<IActionResult> ListBundlePolicies(string bundleId, CancellationToken cancellationToken)
    {
        var bundleResult = await bundles.GetBundleAsync(bundleId, cancellationToken);
        if (bundleResult.Status != BundleOpStatus.Ok)
            return MapBundleError(new BundleOpResult(bundleResult.Status, bundleResult.Error));

        var policies = await ListPoliciesInternalAsync(cancellationToken);
        if (policies.Response is not null) return policies.Response;
        return Ok(ToMemberPolicies(bundleId, policies.Policies!));
    }

    [HttpPost("bundles")]
    [Endpoint(EndpointIds.AccessControlBundlesCreate)]
    [EndpointSummary("Create a custom bundle")]
    [EndpointDescription("Creates a user-prefixed custom bundle from selected catalog endpoints, optionally cloning a code-defined system bundle as the initial endpoint baseline.")]
    public async Task<IActionResult> CreateBundle(
        [FromBody] AccessControlCreateBundleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await bundles.CreateBundleAsync(
            request.Id ?? "",
            request.Endpoints ?? [],
            request.CloneFrom,
            cancellationToken);
        return result.Status == BundleOpStatus.Ok
            ? CreatedAtAction(nameof(GetBundle), new { bundleId = request.Id }, new
            {
                id = request.Id,
                name = string.IsNullOrWhiteSpace(request.Name) ? request.Id : request.Name.Trim(),
                cloneFrom = string.IsNullOrWhiteSpace(request.CloneFrom) ? null : request.CloneFrom.Trim()
            })
            : MapBundleError(result);
    }

    [HttpPut("bundles/{bundleId}/endpoints")]
    [Endpoint(EndpointIds.AccessControlBundlesSetEndpoints)]
    [EndpointSummary("Replace custom bundle endpoints")]
    [EndpointDescription("Replaces a custom bundle's endpoint membership with code-defined catalog ids; seeded system bundles remain immutable and return a forbidden response.")]
    public async Task<IActionResult> SetBundleEndpoints(
        string bundleId,
        [FromBody] AccessControlSetEndpointsRequest request,
        CancellationToken cancellationToken)
        => MapBundle(await bundles.SetBundleEndpointsAsync(bundleId, request.Endpoints ?? [], cancellationToken));

    [HttpDelete("bundles/{bundleId}")]
    [Endpoint(EndpointIds.AccessControlBundlesDelete)]
    [EndpointSummary("Delete an unreferenced custom bundle")]
    [EndpointDescription("Deletes a custom bundle only when no access policy references it; referenced bundles return conflict with the blocking policy identifiers and names.")]
    public async Task<IActionResult> DeleteBundle(string bundleId, CancellationToken cancellationToken)
    {
        var policies = await ListPoliciesInternalAsync(cancellationToken);
        if (policies.Response is not null) return policies.Response;

        var references = ToMemberPolicies(bundleId, policies.Policies!);
        if (references.Count > 0)
        {
            return Conflict(new
            {
                error = $"Bundle '{bundleId}' is referenced by {references.Count} access policy/policies.",
                policies = references
            });
        }

        return MapBundle(await bundles.DeleteBundleAsync(bundleId, cancellationToken));
    }

    [HttpGet("policies")]
    [Endpoint(EndpointIds.AccessControlPoliciesList)]
    [EndpointSummary("List unified access policies")]
    [EndpointDescription("Lists named access policies with endpoint-bundle grants, media deny scopes, assignments, lifecycle state, and OpenFGA synchronization status.")]
    public async Task<IActionResult> ListPolicies(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            AccessPolicySubjects.List, new AccessPolicyListRequestMessage(), cancellationToken);
        if (response is null) return Unavailable();
        if (!response.Success) return MapError(response);
        return Ok(await WithDisplayNamesAsync(response.Policies ?? [], cancellationToken));
    }

    [HttpGet("policies/{policyId:guid}")]
    [Endpoint(EndpointIds.AccessControlPoliciesGet)]
    [EndpointSummary("Get a unified access policy")]
    [EndpointDescription("Returns one named policy with its endpoint bundles, denied media GUIDs, denied providers, inclusive age deny tiers, assignments, and synchronization state.")]
    public async Task<IActionResult> GetPolicy(Guid policyId, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            AccessPolicySubjects.Get,
            new AccessPolicyGetRequestMessage { PolicyId = policyId },
            cancellationToken);
        if (response is null) return Unavailable();
        if (!response.Success) return MapError(response);
        var enriched = await WithDisplayNamesAsync([response.Policy!], cancellationToken);
        return Ok(enriched[0]);
    }

    [HttpPost("policies")]
    [Endpoint(EndpointIds.AccessControlPoliciesCreate)]
    [EndpointSummary("Create a unified access policy")]
    [EndpointDescription("Creates a policy whose bundles grant endpoints and whose media scopes deny playback. An OpenFGA mirror failure returns 202 with syncStatus Failed for background reconciliation.")]
    public async Task<IActionResult> CreatePolicy(
        [FromBody] AccessPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateBundlesAsync(request.BundleIds, cancellationToken);
        if (validation is not null) return validation;

        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
        var subject = AuthConstants.FindSubject(User);
        var policy = request.ToDto(Guid.NewGuid(), now, subject);
        var saved = await SaveAndSyncAsync(policy, cancellationToken);
        return saved.Result is null
            ? StatusCode(saved.StatusCode, saved.Error)
            : saved.StatusCode == StatusCodes.Status202Accepted
                ? Accepted(saved.Result)
                : CreatedAtAction(nameof(GetPolicy), new { policyId = saved.Result.PolicyId }, saved.Result);
    }

    [HttpPut("policies/{policyId:guid}")]
    [Endpoint(EndpointIds.AccessControlPoliciesUpdate)]
    [EndpointSummary("Replace a unified access policy")]
    [EndpointDescription("Replaces endpoint grants, media denies, and assignments. An OpenFGA mirror failure returns 202 with syncStatus Failed for background reconciliation.")]
    public async Task<IActionResult> UpdatePolicy(
        Guid policyId,
        [FromBody] AccessPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await GetPolicyInternalAsync(policyId, cancellationToken);
        if (existing.Response is not null) return existing.Response;
        var validation = await ValidateBundlesAsync(request.BundleIds, cancellationToken);
        if (validation is not null) return validation;

        var current = existing.Policy!;
        var policy = request.ToDto(policyId, current.CreatedAt, current.CreatedBySubject) with
        {
            UpdatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            UpdatedBySubject = AuthConstants.FindSubject(User)
        };
        var saved = await SaveAndSyncAsync(policy, cancellationToken);
        return saved.Result is null
            ? StatusCode(saved.StatusCode, saved.Error)
            : saved.StatusCode == StatusCodes.Status202Accepted
                ? Accepted(saved.Result)
                : Ok(saved.Result);
    }

    [HttpPost("policies/{policyId:guid}/duplicate")]
    [Endpoint(EndpointIds.AccessControlPoliciesDuplicate)]
    [EndpointSummary("Duplicate a unified access policy")]
    [EndpointDescription("Copies scopes and assignments into a disabled policy for review; a deferred OpenFGA mirror returns 202 with its current syncStatus and retry state.")]
    public async Task<IActionResult> DuplicatePolicy(
        Guid policyId,
        [FromBody] AccessPolicyDuplicateRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await GetPolicyInternalAsync(policyId, cancellationToken);
        if (existing.Response is not null) return existing.Response;
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("A name is required.");
        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
        var copy = existing.Policy! with
        {
            PolicyId = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Enabled = false,
            SyncStatus = AccessPolicySyncStatus.Pending,
            SyncError = null,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBySubject = AuthConstants.FindSubject(User),
            UpdatedBySubject = AuthConstants.FindSubject(User)
        };
        var saved = await SaveAndSyncAsync(copy, cancellationToken);
        return saved.Result is null
            ? StatusCode(saved.StatusCode, saved.Error)
            : saved.StatusCode == StatusCodes.Status202Accepted
                ? Accepted(saved.Result)
                : CreatedAtAction(nameof(GetPolicy), new { policyId = saved.Result.PolicyId }, saved.Result);
    }

    [HttpGet("policies/{policyId:guid}/impact")]
    [Endpoint(EndpointIds.AccessControlPoliciesImpact)]
    [EndpointSummary("Preview the impact of an access policy")]
    [EndpointDescription("Resolves a policy's assigned principals and reports counts for endpoint bundles, effective endpoints, denied media, denied providers, and inclusive age tiers.")]
    public async Task<IActionResult> GetPolicyImpact(Guid policyId, CancellationToken cancellationToken)
    {
        var existing = await GetPolicyInternalAsync(policyId, cancellationToken);
        if (existing.Response is not null) return existing.Response;

        var policy = (await WithDisplayNamesAsync([existing.Policy!], cancellationToken))[0];
        var bundleResult = await bundles.ListBundlesAsync(cancellationToken);
        if (bundleResult.Status != BundleOpStatus.Ok)
            return MapBundleError(new BundleOpResult(bundleResult.Status, bundleResult.Error));

        var selectedBundleIds = policy.BundleIds.ToHashSet(StringComparer.Ordinal);
        var endpointCount = bundleResult.Value!
            .Where(bundle => selectedBundleIds.Contains(bundle.Id))
            .SelectMany(bundle => bundle.Endpoints)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return Ok(new AccessPolicyImpactResponse
        {
            PolicyId = policy.PolicyId,
            Assignments = policy.Assignments,
            PrincipalCount = policy.Assignments.Count,
            BundleCount = policy.BundleIds.Count,
            EndpointCount = endpointCount,
            DeniedMediaCount = policy.MediaGuids.Count,
            DeniedProviderCount = policy.Providers.Count,
            AgeTierCount = policy.AgeThresholds.Count
        });
    }

    [HttpDelete("policies/{policyId:guid}")]
    [Endpoint(EndpointIds.AccessControlPoliciesDelete)]
    [EndpointSummary("Delete a unified access policy")]
    [EndpointDescription("Removes policy tuples from OpenFGA before deleting the PostgreSQL policy and all of its media scopes and assignments.")]
    public async Task<IActionResult> DeletePolicy(Guid policyId, CancellationToken cancellationToken)
    {
        var remove = await openFgaPolicies.RemoveAsync(policyId, cancellationToken);
        if (remove.Status != BundleOpStatus.Ok)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, remove.Error);

        var response = await SendAsync(
            AccessPolicySubjects.Delete,
            new AccessPolicyDeleteRequestMessage { PolicyId = policyId },
            cancellationToken);
        if (response is null) return Unavailable();
        return response.Success ? NoContent() : MapError(response);
    }

    [HttpGet("providers")]
    [Endpoint(EndpointIds.AccessControlProvidersList)]
    [EndpointSummary("List providers available to access policies")]
    [EndpointDescription("Returns normalized provider identifiers currently present in media source versions for policy autocomplete.")]
    public async Task<IActionResult> ListProviders(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            AccessPolicySubjects.ProviderCatalog,
            new AccessPolicyListRequestMessage(),
            cancellationToken);
        if (response is null) return Unavailable();
        return response.Success ? Ok(response.Providers ?? []) : MapError(response);
    }

    [HttpGet("media/{mediaGuid:guid}")]
    [Endpoint(EndpointIds.AccessControlMediaSummary)]
    [EndpointSummary("Resolve media information for access management")]
    [EndpointDescription("Resolves a media GUID to its title, normalized providers, and maximum reported age limit for policy editing and effective evaluation.")]
    public async Task<IActionResult> GetMediaSummary(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            AccessPolicySubjects.MediaSummary,
            new AccessPolicyMediaSummaryRequestMessage { MediaGuid = mediaGuid },
            cancellationToken);
        if (response is null) return Unavailable();
        if (!response.Success) return MapError(response);
        return response.MediaSummary?.Found == true ? Ok(response.MediaSummary) : NotFound("Media was not found.");
    }

    [HttpGet("effective")]
    [Endpoint(EndpointIds.AccessControlEffective)]
    [EndpointSummary("Evaluate effective access for a user or group")]
    [EndpointDescription("Returns OpenFGA endpoint access plus assigned source policies and their unioned media GUID, provider, and age-tier deny scopes, regardless of policy sync status.")]
    public async Task<IActionResult> Effective(
        [FromQuery] string principalType,
        [FromQuery] string principalId,
        [FromQuery] string? endpointId,
        [FromQuery] Guid? mediaGuid,
        CancellationToken cancellationToken)
    {
        var evaluation = await EvaluateAccessAsync(
            principalType, principalId, endpointId, mediaGuid, knownGroups: null, cancellationToken);
        return evaluation.Error ?? Ok(evaluation.Result);
    }

    [HttpPost("effective/check")]
    [Endpoint(EndpointIds.AccessControlEffectiveCheck)]
    [EndpointSummary("Check endpoint and media access")]
    [EndpointDescription("Checks an endpoint, a media item, or both for a user or group and returns one combined allow decision with explanatory endpoint, media, provider, and age-axis reasons.")]
    public async Task<IActionResult> CheckEffectiveAccess(
        [FromBody] EffectiveAccessCheckRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EndpointId is null && request.MediaGuid is null)
            return BadRequest("An endpoint id, media GUID, or both are required.");

        var evaluation = await EvaluateAccessAsync(
            request.PrincipalType,
            request.PrincipalId,
            request.EndpointId,
            request.MediaGuid,
            knownGroups: null,
            cancellationToken);
        if (evaluation.Error is not null) return evaluation.Error;

        return Ok(ToCheckResponse(evaluation.Result!));
    }

    [HttpGet("effective/me")]
    [Endpoint(EndpointIds.AccessControlEffectiveMe)]
    [EndpointSummary("Get effective access for the current user")]
    [EndpointDescription("Resolves the authenticated subject and current group claims, then returns the same OpenFGA endpoint grants and database-backed deny-policy provenance as the administrator view.")]
    public async Task<IActionResult> EffectiveMe(
        [FromQuery] string? endpointId,
        [FromQuery] Guid? mediaGuid,
        CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(User);
        if (subject is null) return Unauthorized();

        var groups = User.FindAll(AuthConstants.GroupsClaim)
            .Select(claim => claim.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evaluation = await EvaluateAccessAsync(
            BundleManagementValidation.GranteeTypeUser,
            subject,
            endpointId,
            mediaGuid,
            groups,
            cancellationToken);
        return evaluation.Error ?? Ok(evaluation.Result);
    }

    private async Task<(EffectiveAccessResponse? Result, IActionResult? Error)> EvaluateAccessAsync(
        string principalType,
        string principalId,
        string? endpointId,
        Guid? mediaGuid,
        IReadOnlyList<string>? knownGroups,
        CancellationToken cancellationToken)
    {
        principalType = principalType?.Trim().ToLowerInvariant() ?? "";
        principalId = principalId?.Trim() ?? "";
        endpointId = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId.Trim();
        if (BundleManagementValidation.GranteeUser(principalType, principalId) is null)
            return (null, BadRequest("A valid user or group principal is required."));
        if (endpointId is not null && !EndpointCatalog.Contains(endpointId))
            return (null, BadRequest("The endpoint id is not in the catalog."));

        var endpointResult = await openFgaPolicies.ListEffectiveEndpointsAsync(
            principalType, principalId, cancellationToken);
        if (endpointResult.Status != BundleOpStatus.Ok)
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, endpointResult.Error));

        IReadOnlyList<string> groups;
        if (principalType == BundleManagementValidation.GranteeTypeGroup)
        {
            groups = [principalId];
        }
        else if (knownGroups is not null)
        {
            groups = NormalizeGroups(knownGroups);
        }
        else
        {
            var groupResult = await openFgaPolicies.ListUserGroupsAsync(principalId, cancellationToken);
            if (groupResult.Status != BundleOpStatus.Ok)
                return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, groupResult.Error));
            groups = NormalizeGroups(groupResult.Value ?? []);
        }

        var policiesResult = await ListPoliciesInternalAsync(cancellationToken);
        if (policiesResult.Response is not null) return (null, policiesResult.Response);
        var assignedPolicies = policiesResult.Policies!
            .Where(policy => policy.Enabled && IsAssignedTo(policy, principalType, principalId, groups))
            .OrderBy(policy => policy.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(policy => policy.PolicyId)
            .ToArray();
        var endpointPolicies = assignedPolicies
            .Where(policy => policy.SyncStatus == AccessPolicySyncStatus.Synced)
            .ToArray();

        var bundleResponse = await bundles.ListBundlesAsync(cancellationToken);
        if (bundleResponse.Status != BundleOpStatus.Ok)
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, bundleResponse.Error));
        var bundleViews = bundleResponse.Value!;
        var directBundleIds = bundleViews
            .Where(bundle => bundle.Grants.Any(grant =>
                (grant.Type == principalType && grant.Id == principalId) ||
                (grant.Type == BundleManagementValidation.GranteeTypeGroup &&
                 groups.Contains(grant.Id, StringComparer.OrdinalIgnoreCase))))
            .Select(bundle => bundle.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var policyBundleIds = endpointPolicies
            .SelectMany(policy => policy.BundleIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        AccessPolicyEffectiveMediaDto? media = null;
        if (mediaGuid is { } guid)
        {
            var mediaResponse = await SendAsync(
                AccessPolicySubjects.EffectiveMedia,
                new AccessPolicyEffectiveMediaRequestMessage
                {
                    MediaGuid = guid,
                    UserSubject = principalType == BundleManagementValidation.GranteeTypeUser ? principalId : null,
                    UserGroups = groups
                },
                cancellationToken);
            if (mediaResponse is null || !mediaResponse.Success || mediaResponse.EffectiveMedia is null)
                return (null, Unavailable());
            media = mediaResponse.EffectiveMedia;
        }

        var endpointIds = endpointResult.Value ?? [];
        var endpointDecision = endpointId is null
            ? null
            : BuildEndpointDecision(
                endpointId,
                endpointIds.Contains(endpointId, StringComparer.Ordinal),
                directBundleIds,
                endpointPolicies,
                bundleViews);
        var sourcePolicies = assignedPolicies.Select(ToEffectivePolicySource).ToArray();
        var result = new EffectiveAccessResponse
        {
            PrincipalType = principalType,
            PrincipalId = principalId,
            Groups = groups,
            PolicyIds = assignedPolicies.Select(policy => policy.PolicyId).ToArray(),
            EndpointPolicyIds = endpointPolicies
                .Where(policy => policy.BundleIds.Count > 0)
                .Select(policy => policy.PolicyId)
                .ToArray(),
            DenyPolicyIds = assignedPolicies
                .Where(HasDenyScopes)
                .Select(policy => policy.PolicyId)
                .ToArray(),
            DirectBundleIds = directBundleIds,
            PolicyBundleIds = policyBundleIds,
            BundleIds = directBundleIds.Concat(policyBundleIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            EndpointIds = endpointIds,
            EndpointId = endpointId,
            EndpointAllowed = endpointDecision?.Allowed,
            EndpointDecision = endpointDecision,
            DeniedMediaGuids = assignedPolicies
                .SelectMany(policy => policy.MediaGuids)
                .Distinct()
                .Order()
                .ToArray(),
            DeniedProviders = assignedPolicies
                .SelectMany(policy => policy.Providers)
                .Select(provider => provider.Trim().ToLowerInvariant())
                .Where(provider => provider.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            DeniedAgeThresholds = assignedPolicies
                .SelectMany(policy => policy.AgeThresholds)
                .Distinct()
                .Order()
                .ToArray(),
            SourcePolicies = sourcePolicies,
            Media = media
        };
        return (result, null);
    }

    private async Task<(AccessPolicyDto? Policy, IActionResult? Response)> GetPolicyInternalAsync(
        Guid policyId, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            AccessPolicySubjects.Get,
            new AccessPolicyGetRequestMessage { PolicyId = policyId },
            cancellationToken);
        if (response is null) return (null, Unavailable());
        return response.Success ? (response.Policy, null) : (null, MapError(response));
    }

    private async Task<(IReadOnlyList<AccessPolicyDto>? Policies, IActionResult? Response)> ListPoliciesInternalAsync(
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            AccessPolicySubjects.List, new AccessPolicyListRequestMessage(), cancellationToken);
        if (response is null) return (null, Unavailable());
        return response.Success
            ? (response.Policies ?? [], null)
            : (null, MapError(response));
    }

    private async Task<(AccessPolicyDto? Result, int StatusCode, string? Error)> SaveAndSyncAsync(
        AccessPolicyDto policy,
        CancellationToken cancellationToken)
    {
        var save = await SendAsync(
            AccessPolicySubjects.Save,
            new AccessPolicySaveRequestMessage { Policy = policy },
            cancellationToken);
        if (save is null || !save.Success || save.Policy is null)
            return (
                null,
                save is null ? 503 : save.ErrorCode == "validation" ? 400 : 500,
                save?.ErrorMessage ?? "Access-policy storage is unavailable.");

        var sync = await openFgaPolicies.SynchronizeAsync(save.Policy, cancellationToken);
        var status = sync.Status == BundleOpStatus.Ok
            ? AccessPolicySyncStatus.Synced
            : AccessPolicySyncStatus.Failed;
        var marked = await SendAsync(
            AccessPolicySubjects.SetSync,
            new AccessPolicySetSyncRequestMessage
            {
                PolicyId = save.Policy.PolicyId,
                Version = save.Policy.Version,
                Status = status,
                Error = sync.Error
            },
            cancellationToken);
        var result = marked?.Policy ?? save.Policy with { SyncStatus = status, SyncError = sync.Error };
        return (result, status == AccessPolicySyncStatus.Synced ? 200 : 202, sync.Error);
    }

    private async Task<IActionResult?> ValidateBundlesAsync(
        IReadOnlyList<string> bundleIds,
        CancellationToken cancellationToken)
    {
        var available = await bundles.ListBundlesAsync(cancellationToken);
        if (available.Status != BundleOpStatus.Ok)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, available.Error);
        var known = available.Value!.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = bundleIds.Where(x => !known.Contains(x)).Distinct().ToArray();
        return unknown.Length == 0 ? null : BadRequest($"Unknown bundle id(s): {string.Join(", ", unknown)}.");
    }

    private async Task<IReadOnlyList<AccessPolicyDto>> WithDisplayNamesAsync(
        IReadOnlyList<AccessPolicyDto> policies,
        CancellationToken cancellationToken)
    {
        var userIds = policies.SelectMany(x => x.Assignments)
            .Where(x => x.Type == "user").Select(x => x.Id).Distinct().ToArray();
        var names = await directory.ResolveUserNamesAsync(userIds, cancellationToken);
        return policies.Select(policy => policy with
        {
            Assignments = policy.Assignments.Select(assignment =>
                assignment.Type == "user" && names.TryGetValue(assignment.Id, out var name)
                    ? assignment with { DisplayName = name }
                    : assignment).ToArray()
        }).ToArray();
    }

    private async Task<AccessPolicyOperationResponseMessage?> SendAsync<T>(
        string subject, T request, CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<T, AccessPolicyOperationResponseMessage>(
                subject, request, RequestTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Access-control request failed on {Subject}.", subject);
            return null;
        }
    }

    private IActionResult MapError(AccessPolicyOperationResponseMessage response) => response.ErrorCode switch
    {
        "validation" => BadRequest(response.ErrorMessage),
        "not_found" => NotFound(response.ErrorMessage),
        _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage)
    };

    private static AccessControlBundleView ToBundleView(
        BundleView bundle,
        IReadOnlyList<AccessPolicyDto> policies)
    {
        var memberPolicies = ToMemberPolicies(bundle.Id, policies);
        return new AccessControlBundleView
        {
            Id = bundle.Id,
            SystemOwned = bundle.SystemOwned,
            Endpoints = bundle.Endpoints,
            EndpointCount = bundle.Endpoints.Count,
            PolicyCount = memberPolicies.Count,
            MemberPolicies = memberPolicies
        };
    }

    private static IReadOnlyList<AccessControlBundlePolicyView> ToMemberPolicies(
        string bundleId,
        IReadOnlyList<AccessPolicyDto> policies)
        => policies
            .Where(policy => policy.BundleIds.Contains(bundleId, StringComparer.Ordinal))
            .OrderBy(policy => policy.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(policy => policy.PolicyId)
            .Select(policy => new AccessControlBundlePolicyView
            {
                PolicyId = policy.PolicyId,
                Name = policy.Name,
                Enabled = policy.Enabled,
                SyncStatus = policy.SyncStatus
            })
            .ToArray();

    private static IReadOnlyList<string> NormalizeGroups(IEnumerable<string> groups)
        => groups
            .Select(group => group.Trim())
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsAssignedTo(
        AccessPolicyDto policy,
        string principalType,
        string principalId,
        IReadOnlyList<string> groups)
        => policy.Assignments.Any(assignment =>
            (assignment.Type == principalType &&
             string.Equals(assignment.Id, principalId, StringComparison.Ordinal)) ||
            (assignment.Type == BundleManagementValidation.GranteeTypeGroup &&
             groups.Contains(assignment.Id, StringComparer.OrdinalIgnoreCase)));

    private static bool HasDenyScopes(AccessPolicyDto policy)
        => policy.MediaGuids.Count > 0 ||
           policy.Providers.Count > 0 ||
           policy.AgeThresholds.Count > 0;

    private static EffectiveAccessPolicySource ToEffectivePolicySource(AccessPolicyDto policy)
        => new()
        {
            PolicyId = policy.PolicyId,
            Name = policy.Name,
            SyncStatus = policy.SyncStatus,
            BundleIds = policy.BundleIds,
            DeniedMediaGuids = policy.MediaGuids,
            DeniedProviders = policy.Providers,
            DeniedAgeThresholds = policy.AgeThresholds,
            ContributesEndpoints = policy.SyncStatus == AccessPolicySyncStatus.Synced &&
                                   policy.BundleIds.Count > 0,
            ContributesDenies = HasDenyScopes(policy)
        };

    private static AccessPolicyAxisDecisionDto BuildEndpointDecision(
        string endpointId,
        bool allowed,
        IReadOnlyList<string> directBundleIds,
        IReadOnlyList<AccessPolicyDto> endpointPolicies,
        IReadOnlyList<BundleView> bundles)
    {
        var endpointBundleIds = bundles
            .Where(bundle => bundle.Endpoints.Contains(endpointId, StringComparer.Ordinal))
            .Select(bundle => bundle.Id)
            .ToHashSet(StringComparer.Ordinal);
        var grantingPolicyIds = endpointPolicies
            .Where(policy => policy.BundleIds.Any(endpointBundleIds.Contains))
            .Select(policy => policy.PolicyId)
            .Distinct()
            .Order()
            .ToArray();
        var matchingDirectBundles = directBundleIds
            .Where(endpointBundleIds.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var provenance = grantingPolicyIds.Length > 0 && matchingDirectBundles.Length > 0
            ? " through assigned policies and transitional direct bundle grants"
            : grantingPolicyIds.Length > 0
                ? " through assigned policies"
                : matchingDirectBundles.Length > 0
                    ? " through transitional direct bundle grants"
                    : "";

        return new AccessPolicyAxisDecisionDto
        {
            Axis = "endpoint",
            Resource = endpointId,
            Restricted = !allowed,
            Allowed = allowed,
            MatchingPolicyIds = grantingPolicyIds,
            GrantingPolicyIds = grantingPolicyIds,
            Reason = allowed
                ? $"OpenFGA allows invocation of endpoint '{endpointId}'{provenance}."
                : $"OpenFGA does not allow invocation of endpoint '{endpointId}'."
        };
    }

    private static EffectiveAccessCheckResponse ToCheckResponse(EffectiveAccessResponse access)
    {
        var decisions = new List<AccessPolicyAxisDecisionDto>();
        if (access.EndpointDecision is not null)
            decisions.Add(access.EndpointDecision);
        if (access.Media is { Found: true } media)
        {
            decisions.AddRange(media.Decisions);
        }
        else if (access.Media is not null)
        {
            decisions.Add(new AccessPolicyAxisDecisionDto
            {
                Axis = "media",
                Resource = access.Media.MediaGuid.ToString(),
                Restricted = true,
                Allowed = false,
                Reason = "The media GUID was not found."
            });
        }

        return new EffectiveAccessCheckResponse
        {
            PrincipalType = access.PrincipalType,
            PrincipalId = access.PrincipalId,
            IsAllowed = decisions.Count > 0 && decisions.All(decision => decision.Allowed),
            Decisions = decisions,
            Media = access.Media,
            SourcePolicyIds = access.PolicyIds
        };
    }

    private IActionResult MapBundle(BundleOpResult result)
        => result.Status == BundleOpStatus.Ok ? NoContent() : MapBundleError(result);

    private IActionResult MapBundleError(BundleOpResult result) => result.Status switch
    {
        BundleOpStatus.NotFound => NotFound(result.Error),
        BundleOpStatus.Validation => BadRequest(result.Error),
        BundleOpStatus.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result.Error),
        BundleOpStatus.Unavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result.Error),
        _ => StatusCode(StatusCodes.Status500InternalServerError, result.Error ?? "Bundle management failed.")
    };

    private IActionResult Unavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "Access-policy storage is unavailable.");
}

public sealed record AccessControlCreateBundleRequest
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? CloneFrom { get; init; }
    public IReadOnlyList<string>? Endpoints { get; init; }
}

public sealed record AccessControlSetEndpointsRequest
{
    public IReadOnlyList<string>? Endpoints { get; init; }
}

public sealed record AccessControlBundlePolicyView
{
    public required Guid PolicyId { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public AccessPolicySyncStatus SyncStatus { get; init; }
}

public sealed record AccessControlBundleView
{
    public required string Id { get; init; }
    public bool SystemOwned { get; init; }
    public IReadOnlyList<string> Endpoints { get; init; } = [];
    public int EndpointCount { get; init; }
    public int PolicyCount { get; init; }
    public IReadOnlyList<AccessControlBundlePolicyView> MemberPolicies { get; init; } = [];
}

public sealed record AccessPolicyWriteRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> BundleIds { get; init; } = [];
    public IReadOnlyList<Guid> MediaGuids { get; init; } = [];
    public IReadOnlyList<string> Providers { get; init; } = [];
    public IReadOnlyList<int> AgeThresholds { get; init; } = [];
    public IReadOnlyList<AccessPolicyAssignmentDto> Assignments { get; init; } = [];

    public AccessPolicyDto ToDto(Guid id, NodaTime.Instant createdAt, string? createdBy) => new()
    {
        PolicyId = id,
        Name = Name,
        Description = Description,
        Enabled = Enabled,
        SyncStatus = AccessPolicySyncStatus.Pending,
        BundleIds = BundleIds,
        MediaGuids = MediaGuids,
        Providers = Providers,
        AgeThresholds = AgeThresholds,
        Assignments = Assignments,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        CreatedBySubject = createdBy,
        UpdatedBySubject = createdBy
    };
}

public sealed record AccessPolicyDuplicateRequest
{
    public required string Name { get; init; }
}

public sealed record AccessPolicyImpactResponse
{
    public required Guid PolicyId { get; init; }
    public IReadOnlyList<AccessPolicyAssignmentDto> Assignments { get; init; } = [];
    public int PrincipalCount { get; init; }
    public int BundleCount { get; init; }
    public int EndpointCount { get; init; }
    public int DeniedMediaCount { get; init; }
    public int DeniedProviderCount { get; init; }
    public int AgeTierCount { get; init; }
}

public sealed record EffectiveAccessResponse
{
    public required string PrincipalType { get; init; }
    public required string PrincipalId { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<Guid> PolicyIds { get; init; } = [];
    public IReadOnlyList<Guid> EndpointPolicyIds { get; init; } = [];
    public IReadOnlyList<Guid> DenyPolicyIds { get; init; } = [];
    public IReadOnlyList<string> DirectBundleIds { get; init; } = [];
    public IReadOnlyList<string> PolicyBundleIds { get; init; } = [];
    public IReadOnlyList<string> BundleIds { get; init; } = [];
    public IReadOnlyList<string> EndpointIds { get; init; } = [];
    public string? EndpointId { get; init; }
    public bool? EndpointAllowed { get; init; }
    public AccessPolicyAxisDecisionDto? EndpointDecision { get; init; }
    public IReadOnlyList<Guid> DeniedMediaGuids { get; init; } = [];
    public IReadOnlyList<string> DeniedProviders { get; init; } = [];
    public IReadOnlyList<int> DeniedAgeThresholds { get; init; } = [];
    public IReadOnlyList<EffectiveAccessPolicySource> SourcePolicies { get; init; } = [];
    public AccessPolicyEffectiveMediaDto? Media { get; init; }
}

public sealed record EffectiveAccessPolicySource
{
    public required Guid PolicyId { get; init; }
    public required string Name { get; init; }
    public AccessPolicySyncStatus SyncStatus { get; init; }
    public IReadOnlyList<string> BundleIds { get; init; } = [];
    public IReadOnlyList<Guid> DeniedMediaGuids { get; init; } = [];
    public IReadOnlyList<string> DeniedProviders { get; init; } = [];
    public IReadOnlyList<int> DeniedAgeThresholds { get; init; } = [];
    public bool ContributesEndpoints { get; init; }
    public bool ContributesDenies { get; init; }
}

public sealed record EffectiveAccessCheckRequest
{
    public required string PrincipalType { get; init; }
    public required string PrincipalId { get; init; }
    public string? EndpointId { get; init; }
    public Guid? MediaGuid { get; init; }
}

public sealed record EffectiveAccessCheckResponse
{
    public required string PrincipalType { get; init; }
    public required string PrincipalId { get; init; }
    public bool IsAllowed { get; init; }
    public IReadOnlyList<AccessPolicyAxisDecisionDto> Decisions { get; init; } = [];
    public AccessPolicyEffectiveMediaDto? Media { get; init; }
    public IReadOnlyList<Guid> SourcePolicyIds { get; init; } = [];
}
