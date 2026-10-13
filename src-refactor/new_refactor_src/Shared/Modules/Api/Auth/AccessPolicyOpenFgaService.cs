using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shared.Messaging;

namespace WebAPI.Auth;

public interface IAccessPolicyOpenFgaService
{
    Task<BundleOpResult> SynchronizeAsync(AccessPolicyDto policy, CancellationToken cancellationToken);
    Task<BundleOpResult> RemoveAsync(Guid policyId, CancellationToken cancellationToken);
    Task<BundleOpResult<IReadOnlyList<string>>> ListEffectiveEndpointsAsync(
        string principalType, string principalId, CancellationToken cancellationToken);
    Task<BundleOpResult<IReadOnlyList<string>>> ListUserGroupsAsync(
        string userSubject, CancellationToken cancellationToken);
}
