using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebAPI.Auth;

public sealed class AuthentikOptions
{
    public const string SectionName = "Authentik";

    /// <summary>Base URL of the Authentik server (e.g. http://localhost:9000).</summary>
    public string ApiUrl { get; init; } = "";

    /// <summary>Authentik API token used for directory lookups (core/users, core/groups).</summary>
    public string? ApiToken { get; init; }
}

/// <summary>
/// Directory search against the Authentik Admin API. Users are searched by username/name/email and
/// return their UUID — the OIDC <c>sub</c> under the provider's <c>sub_mode: user_uuid</c>, i.e. the
/// id OpenFGA user grants are keyed by. Groups return the group name, matching the tuple writer's
/// <c>group:&lt;name&gt;#member</c> subjects. Failures degrade to an empty result set: the search is
/// a typing aid, and manual grantee entry must keep working when Authentik is unreachable.
/// </summary>
public sealed class AuthentikDirectoryService(
    HttpClient httpClient,
    IOptions<AuthentikOptions> options,
    ILogger<AuthentikDirectoryService> logger) : IDirectoryService
{
    private const int PageSize = 10;

    private readonly AuthentikOptions _options = options.Value;

    public async Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string granteeType, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiUrl) || string.IsNullOrWhiteSpace(_options.ApiToken) || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            return granteeType switch
            {
                BundleManagementValidation.GranteeTypeUser => await SearchUsersAsync(query.Trim(), cancellationToken),
                BundleManagementValidation.GranteeTypeGroup => await SearchGroupsAsync(query.Trim(), cancellationToken),
                _ => []
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Authentik directory search failed for {GranteeType} query.", granteeType);
            return [];
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveUserNamesAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_options.ApiUrl) || string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return names;
        }

        // The core/users uuid filter only accepts a single value, but grant lists are small enough
        // that one request per distinct id is fine. Failures leave ids unresolved rather than erroring.
        foreach (var userId in userIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = await GetAsync($"/api/v3/core/users/?uuid={Uri.EscapeDataString(userId)}&page_size=1", cancellationToken);
                foreach (var user in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var username = user.TryGetProperty("username", out var usernameProp) ? usernameProp.GetString() : null;
                    var name = user.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (FirstNonBlank(username, name) is { } display)
                    {
                        names[userId] = display;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Authentik user name resolution failed for a grant subject.");
            }
        }

        return names;
    }

    private async Task<IReadOnlyList<DirectoryEntry>> SearchUsersAsync(string query, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync($"/api/v3/core/users/?search={Uri.EscapeDataString(query)}&page_size={PageSize}", cancellationToken);
        var results = new List<DirectoryEntry>();
        foreach (var user in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var uuid = user.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(uuid))
            {
                continue;
            }

            var username = user.TryGetProperty("username", out var usernameProp) ? usernameProp.GetString() : null;
            var name = user.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var email = user.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
            results.Add(new DirectoryEntry(
                BundleManagementValidation.GranteeTypeUser,
                uuid,
                string.IsNullOrWhiteSpace(username) ? uuid : username,
                Description: FirstNonBlank(name, email)));
        }

        return results;
    }

    private async Task<IReadOnlyList<DirectoryEntry>> SearchGroupsAsync(string query, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(
            $"/api/v3/core/groups/?search={Uri.EscapeDataString(query)}&page_size={PageSize}&include_users=false",
            cancellationToken);
        var results = new List<DirectoryEntry>();
        foreach (var group in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var name = group.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Grant subjects must be valid OpenFGA object ids; groups with other names (e.g.
            // "authentik Admins") are skipped by the tuple writer and would never match a grant.
            if (!BundleManagementValidation.ValidIdRegex().IsMatch(name))
            {
                continue;
            }

            // include_users=false always serializes `users` as [] regardless of membership, so no
            // member count is available without a heavier query.
            results.Add(new DirectoryEntry(BundleManagementValidation.GranteeTypeGroup, name, name));
        }

        return results;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
