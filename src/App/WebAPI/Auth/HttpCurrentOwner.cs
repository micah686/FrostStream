using Shared.Application;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed class HttpCurrentOwner(IHttpContextAccessor httpContextAccessor) : ICurrentOwner
{
    public string? Subject => AuthConstants.FindSubject(httpContextAccessor.HttpContext?.User);
}
