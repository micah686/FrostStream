using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WebAPI.Auth;

/// <summary>
/// Reconciles a user's Authentik group membership into OpenFGA <c>group:&lt;name&gt;#member</c> tuples.
/// Adding a user to a group writes a tuple; removing them from a group deletes the stale tuple so
/// revoked memberships stop granting access.
/// </summary>
public interface IOpenFgaTupleWriter
{
    Task SyncUserGroupsAsync(string subject, IReadOnlyCollection<string> groups, CancellationToken cancellationToken = default);
}

/// <summary>Single-user-mode no-op; there is no external authorization server to sync to.</summary>
