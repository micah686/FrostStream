using System.Text.RegularExpressions;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed class NullDirectoryService : IDirectoryService
{
    public Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string granteeType, string query, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DirectoryEntry>>([]);

    public Task<IReadOnlyDictionary<string, string>> ResolveUserNamesAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}
