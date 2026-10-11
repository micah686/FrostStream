using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Shared.Auth;
using Shared.Deployment;
using WebAPI.Auth;
using WebAPI.Features.Media;
namespace FrostStream.Lite.Infrastructure;
public static class LiteIdentityRegistration
{
    public static IHostApplicationBuilder AddLiteIdentity(this IHostApplicationBuilder builder)
    {
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Lite) throw new InvalidOperationException("Local identity requires Lite.");
        if (!builder.Services.TryAddModule(typeof(LiteIdentityRegistration))) return builder;
        builder.Services.AddAuthentication(AuthConstants.SingleUserScheme).AddScheme<AuthenticationSchemeOptions, SingleUserAuthenticationHandler>(AuthConstants.SingleUserScheme, _ => { });
        builder.Services.AddSingleton<IFrostStreamAuthorizer, AllowAllAuthorizer>();
        builder.Services.AddSingleton<IOpenFgaTupleWriter, NullOpenFgaTupleWriter>();
        builder.Services.AddSingleton<IBundleManagementService, NullBundleManagementService>();
        builder.Services.AddSingleton<IAccessPolicyOpenFgaService, NullAccessPolicyOpenFgaService>();
        builder.Services.AddSingleton<IDirectoryService, NullDirectoryService>();
        builder.Services.AddScoped<ISessionSynchronizationService, SessionSynchronizationService>();
        builder.Services.AddAuthorization(AuthPolicies.AddFrostStreamPolicies);
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, LiteAuthorizationPolicyProvider>();
        builder.Services.AddSingleton<IAuthorizationService, LiteAuthorizationService>();
        builder.Services.AddTransient<Microsoft.AspNetCore.Authorization.Policy.IPolicyEvaluator, LitePolicyEvaluator>();
        builder.Services.AddScoped<IMediaAccessChecker, LiteMediaAccessChecker>();
        builder.Services.AddControllers().AddApplicationPart(typeof(LiteIdentityRegistration).Assembly);
        return builder;
    }
}
