using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WebAPI.Auth;

public sealed class NullOpenFgaTupleWriter : IOpenFgaTupleWriter
{
    public Task SyncUserGroupsAsync(string subject, IReadOnlyCollection<string> groups, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
