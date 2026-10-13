using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shared.Messaging;

namespace WebAPI.Auth;

public sealed class NullAccessPolicyOpenFgaService : IAccessPolicyOpenFgaService
{
    public Task<BundleOpResult> SynchronizeAsync(AccessPolicyDto policy, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult.Ok);

    public Task<BundleOpResult> RemoveAsync(Guid policyId, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult.Ok);

    public Task<BundleOpResult<IReadOnlyList<string>>> ListEffectiveEndpointsAsync(
        string principalType, string principalId, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult<IReadOnlyList<string>>.Ok(
            EndpointCatalog.Endpoints.Select(x => x.Id).ToArray()));

    public Task<BundleOpResult<IReadOnlyList<string>>> ListUserGroupsAsync(
        string userSubject, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult<IReadOnlyList<string>>.Ok(["admins"]));
}
