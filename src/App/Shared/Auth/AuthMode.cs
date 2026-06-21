using Microsoft.Extensions.Configuration;

namespace Shared.Auth;

public static class AuthMode
{
    public static bool IsSingleUserMode(IConfiguration configuration)
        => IsTruthy(configuration["SINGLE_USER_MODE"]) ||
           IsTruthy(configuration["Auth:SingleUserMode"]);

    public static bool IsTruthy(string? value)
        => value is not null &&
           (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
}
