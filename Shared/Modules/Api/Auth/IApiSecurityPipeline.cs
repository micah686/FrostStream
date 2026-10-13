namespace WebAPI.Auth;
/// <summary>Edition-owned security middleware runs between authentication and authorization.</summary>
public interface IApiSecurityPipeline { void Configure(WebApplication app); }
