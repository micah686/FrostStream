using WebAPI.Auth;
namespace FrostStream.Full.Security;
internal sealed class FullSecurityPipeline : IApiSecurityPipeline
{
    public void Configure(WebApplication app) => app.UseMiddleware<CsrfProtectionMiddleware>();
}
