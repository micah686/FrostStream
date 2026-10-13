
namespace Shared.Messaging;

/// <summary>
/// Server-side browser authentication state. DataBridge provisions this bucket; WebAPI only
/// consumes it so browser sessions never introduce a PostgreSQL dependency into the API.
/// </summary>
public static class AuthSessionsTopology
{
    public const string BucketNameValue = "FROSTSTREAM_AUTH_SESSIONS";

}
