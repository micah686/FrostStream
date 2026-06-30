namespace WebAPI.Auth;

public static class WebApiHardening
{
    public static void ValidateStartup(FrostStreamAuthOptions options, bool singleUserMode, bool isProduction)
    {
        if (isProduction && singleUserMode && !options.AllowSingleUserModeInProduction)
        {
            throw new InvalidOperationException(
                "SINGLE_USER_MODE is not allowed in production. Set Auth:AllowSingleUserModeInProduction=true only for an intentionally isolated deployment.");
        }
    }
}
