using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Backups;
using Shared.LiveChat;
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;
using WebAPI.Auth;
using WebAPI.Features.BackgroundJobs;
using WebAPI.Features.Backups;
using WebAPI.Features.Downloads;
using WebAPI.Features.Media;
using WebAPI.Features.Media.Casting;

using Shared.Deployment;
using Microsoft.Extensions.Hosting;

namespace FrostStream.Full.Security;
public static class SecurityRegistration
{
    public static IHostApplicationBuilder AddFullSecurity(this IHostApplicationBuilder builder)
    {
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Full)
            throw new InvalidOperationException("Full security requires Deployment:Mode=Full.");
        var authOptions = builder.Configuration
            .GetSection(FrostStreamAuthOptions.SectionName)
            .Get<FrostStreamAuthOptions>() ?? new FrostStreamAuthOptions();
        WebApiHardening.ValidateStartup(authOptions, builder.Environment.IsProduction());
        var fga = builder.Configuration.GetSection(OpenFgaOptions.SectionName).Get<OpenFgaOptions>() ?? new();
        if (!Uri.TryCreate(fga.Endpoint, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != "http" && endpoint.Scheme != "https"))
            throw new InvalidOperationException("OpenFga:Endpoint must be an absolute HTTP(S) URI for Full.");
        if (!fga.AutoProvision && (string.IsNullOrWhiteSpace(fga.StoreId) || string.IsNullOrWhiteSpace(fga.AuthorizationModelId)))
            throw new InvalidOperationException("OpenFga:StoreId and OpenFga:AuthorizationModelId are required when auto-provisioning is disabled.");
        EndpointCatalogValidator.Validate(typeof(WebAPI.WebAPIModule).Assembly, typeof(SecurityRegistration).Assembly);
        if (!builder.Services.TryAddModule(typeof(SecurityRegistration))) return builder;
        builder.Services.Configure<FrostStreamAuthOptions>(builder.Configuration.GetSection(FrostStreamAuthOptions.SectionName));
        builder.Services.Configure<OpenFgaOptions>(builder.Configuration.GetSection(OpenFgaOptions.SectionName));
        builder.Services.Configure<AuthentikOptions>(builder.Configuration.GetSection(AuthentikOptions.SectionName));
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName("FrostStream.WebAPI.Bff");
        if (!string.IsNullOrWhiteSpace(authOptions.DataProtectionKeysPath))
        {
            Directory.CreateDirectory(authOptions.DataProtectionKeysPath);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(authOptions.DataProtectionKeysPath));
        }

        // Default scheme is a selector. Explicit, sessionless credentials take precedence over the
        // ambient browser cookie so API and cast clients remain deterministic.
        const string schemeSelector = "FrostStream";
        var authBuilder = builder.Services
            .AddAuthentication(schemeSelector)
            .AddPolicyScheme(schemeSelector, "FrostStream scheme selector", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    if (context.Request.Query.ContainsKey(CastTokenDefaults.QueryParameter))
                    {
                        return CastTokenDefaults.Scheme;
                    }

                    if (context.Request.Query.ContainsKey(PodcastTokenDefaults.QueryParameter))
                    {
                        return PodcastTokenDefaults.Scheme;
                    }

                    if (context.Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        return JwtBearerDefaults.AuthenticationScheme;
                    }

                    if (context.Request.Cookies.ContainsKey(BffAuthenticationDefaults.CookieName))
                    {
                        return BffAuthenticationDefaults.CookieScheme;
                    }

                    return BffAuthenticationDefaults.CookieScheme;
                };
            });
        var authority = authOptions.Authority;
        var audience = authOptions.Audience;
        // Authentik defaults the token `aud` to the OIDC client_id. The froststream blueprint
        // also ships a custom mapping that can emit `froststream-api`, so accept both: the
        // configured API audience and the client_id when one is supplied.
        var validAudiences = new[] { audience, authOptions.ClientId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        authBuilder
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudiences = validAudiences,
                    NameClaimType = AuthConstants.PreferredUsernameClaim,
                    RoleClaimType = AuthConstants.GroupsClaim
                };
            })
            .AddCookie(BffAuthenticationDefaults.CookieScheme, options =>
            {
                options.Cookie.Name = BffAuthenticationDefaults.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = authOptions.SecureCookies
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(authOptions.SessionLifetimeDays);
                options.SlidingExpiration = false;
                options.EventsType = typeof(BffCookieEvents);
            })
            .AddOpenIdConnect(BffAuthenticationDefaults.OpenIdConnectScheme, options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
                options.ClientId = authOptions.ClientId;
                options.ClientSecret = authOptions.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.SignInScheme = BffAuthenticationDefaults.CookieScheme;
                options.CallbackPath = "/auth/callback";
                options.TokenValidationParameters.NameClaimType = AuthConstants.PreferredUsernameClaim;
                options.TokenValidationParameters.RoleClaimType = AuthConstants.GroupsClaim;
                options.Scope.Clear();
                foreach (var scope in authOptions.Scopes.Split(' ',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    options.Scope.Add(scope);
                }

                var transientSecurePolicy = authOptions.SecureCookies
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = transientSecurePolicy;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = transientSecurePolicy;
                options.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri =
                            $"{authOptions.PublicOrigin.TrimEnd('/')}/auth/callback";
                        context.ProtocolMessage.IssuerAddress = RewriteBrowserEndpoint(
                            context.ProtocolMessage.IssuerAddress,
                            string.IsNullOrWhiteSpace(authOptions.PublicAuthority)
                                ? authOptions.Authority
                                : authOptions.PublicAuthority);
                        return Task.CompletedTask;
                    },
                    OnTicketReceived = async context =>
                    {
                        var accessToken = context.Properties?.GetTokenValue("access_token");
                        if (string.IsNullOrWhiteSpace(accessToken))
                        {
                            context.Fail("OIDC token response did not contain an access token.");
                            return;
                        }

                        try
                        {
                            var validator = context.HttpContext.RequestServices
                                .GetRequiredService<IAccessTokenValidator>();
                            var principal = await validator.ValidateAsync(
                                accessToken,
                                context.HttpContext.RequestAborted);
                            context.Principal = principal;
                            context.Properties!.IsPersistent = true;
                            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow
                                .AddDays(authOptions.SessionLifetimeDays);

                            var sync = await context.HttpContext.RequestServices
                                .GetRequiredService<ISessionSynchronizationService>()
                                .SynchronizeAsync(principal, context.HttpContext.RequestAborted);
                            if (!sync.Success)
                            {
                                context.HttpContext.RequestServices
                                    .GetRequiredService<ILoggerFactory>().CreateLogger("WebAPI.Oidc")
                                    .LogWarning("Session synchronization after login failed for subject {Subject}: {Error}",
                                        AuthConstants.FindSubject(principal), sync.ErrorMessage);
                            }
                        }
                        catch (Exception ex)
                        {
                            context.Fail(ex);
                        }
                    }
                };
            });

        builder.Services.AddSingleton<NatsBffTicketStore>();
        builder.Services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>, BffCookiePostConfigure>();
        builder.Services.AddScoped<BffCookieEvents>();
        builder.Services.AddSingleton<IAccessTokenValidator, AccessTokenValidator>();
        builder.Services.AddScoped<BffSessionRefreshService>();
        builder.Services.AddHttpClient(BffAuthenticationDefaults.HttpClientName);

        builder.Services.AddSingleton(sp =>
        {
            var openFgaOptions = sp.GetRequiredService<IOptions<OpenFgaOptions>>().Value;
            return new OpenFgaRuntimeState
            {
                StoreId = NullIfBlank(openFgaOptions.StoreId),
                AuthorizationModelId = NullIfBlank(openFgaOptions.AuthorizationModelId)
            };
        });
        builder.Services.AddHttpClient<OpenFgaAuthorizer>();
        builder.Services.AddScoped<IFrostStreamAuthorizer>(sp => sp.GetRequiredService<OpenFgaAuthorizer>());
        builder.Services.AddHttpClient<OpenFgaTupleWriter>();
        builder.Services.AddScoped<IOpenFgaTupleWriter>(sp => sp.GetRequiredService<OpenFgaTupleWriter>());
        builder.Services.AddHttpClient(OpenFgaProvisioner.HttpClientName);
        builder.Services.AddHostedService<OpenFgaProvisioner>();
        builder.Services.AddHttpClient<OpenFgaBundleManagementService>();
        builder.Services.AddScoped<IBundleManagementService>(sp => sp.GetRequiredService<OpenFgaBundleManagementService>());
        builder.Services.AddHttpClient<OpenFgaAccessPolicyService>();
        builder.Services.AddScoped<IAccessPolicyOpenFgaService>(sp => sp.GetRequiredService<OpenFgaAccessPolicyService>());
        builder.Services.AddHttpClient<AuthentikDirectoryService>();
        builder.Services.AddScoped<IDirectoryService>(sp => sp.GetRequiredService<AuthentikDirectoryService>());
        builder.Services.AddHostedService<AccessPolicyReconciliationService>();
        builder.Services.AddScoped<IAuthorizationHandler, FrostStreamPermissionHandler>();
        builder.Services.AddScoped<ISessionSynchronizationService, SessionSynchronizationService>();
        builder.Services.AddAuthorization(AuthPolicies.AddFrostStreamPolicies);
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = BffAuthenticationDefaults.AntiforgeryCookieName;
            options.Cookie.HttpOnly = false;
            options.Cookie.IsEssential = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = authOptions.SecureCookies
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.HeaderName = BffAuthenticationDefaults.AntiforgeryHeaderName;
        });
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, EndpointPolicyProvider>();
        builder.Services.AddScoped<IMediaAccessChecker, MediaAccessChecker>();
        builder.Services.AddSingleton<IApiSecurityPipeline, FullSecurityPipeline>();
        builder.Services.AddControllers().AddApplicationPart(typeof(SecurityRegistration).Assembly);
        return builder;
    }
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string RewriteBrowserEndpoint(string endpoint, string publicAuthority)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var source) ||
            !Uri.TryCreate(publicAuthority, UriKind.Absolute, out var browser))
        {
            return endpoint;
        }

        return new UriBuilder(source)
        {
            Scheme = browser.Scheme,
            Host = browser.Host,
            Port = browser.IsDefaultPort ? -1 : browser.Port
        }.Uri.ToString();
    }
}
