using Conduit.NATS;
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
using Shared.Secrets;
using Shared.Storage;
using System.Text.Json.Serialization;
using WebAPI.Auth;
using WebAPI.Features.Backups;
using WebAPI.Features.Downloads;
using WebAPI.Features.Media;
using WebAPI.Features.Media.Casting;

namespace WebAPI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Fail fast if the [Endpoint] attributes have drifted from the registry (the Finding-1 class
        // of bug). This is the single drift guard between attributes, the catalog, and seeding.
        EndpointCatalogValidator.Validate(typeof(Program).Assembly);

        var singleUserMode = AuthMode.IsSingleUserMode(builder.Configuration);
        var authOptions = builder.Configuration
            .GetSection(FrostStreamAuthOptions.SectionName)
            .Get<FrostStreamAuthOptions>() ?? new FrostStreamAuthOptions();
        WebApiHardening.ValidateStartup(authOptions, singleUserMode, builder.Environment.IsProduction());
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
                    if (context.Request.Headers.ContainsKey(MediaProcessorAuthenticationDefaults.ApiKeyHeader))
                    {
                        return MediaProcessorAuthenticationDefaults.Scheme;
                    }

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

                    return singleUserMode
                        ? AuthConstants.SingleUserScheme
                        : BffAuthenticationDefaults.CookieScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, MediaProcessorAuthenticationHandler>(
                MediaProcessorAuthenticationDefaults.Scheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, CastTokenAuthenticationHandler>(
                CastTokenDefaults.Scheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, PodcastTokenAuthenticationHandler>(
                PodcastTokenDefaults.Scheme,
                _ => { });

        builder.Services.Configure<MediaProcessorAuthOptions>(builder.Configuration.GetSection("MediaProcessor"));
        builder.Services.Configure<CastTokenOptions>(builder.Configuration.GetSection(CastTokenOptions.SectionName));
        builder.Services.Configure<PodcastTokenOptions>(builder.Configuration.GetSection(PodcastTokenOptions.SectionName));
        builder.Services.AddSingleton<CastTokenService>();
        builder.Services.AddSingleton<PodcastTokenService>();
        builder.Services.Configure<CastingOptions>(builder.Configuration.GetSection(CastingOptions.SectionName));
        builder.Services.AddSingleton<CastMediaUrlBuilder>();
        builder.Services.AddSingleton<ICastProtocol, ChromecastCastProtocol>();
        builder.Services.AddSingleton<ICastProtocol, FCastCastProtocol>();
        builder.Services.AddSingleton<ICastDeviceRegistry, CastDeviceRegistry>();
        builder.Services.AddSingleton<CastSessionManager>();
        builder.Services.AddScoped<MediaAccessChecker>();
        builder.Services.AddScoped<AudioRenditionResolver>();
        builder.Services.AddScoped<ChannelAudioResolver>();
        builder.Services.AddScoped<StreamRenditionResolver>();
        builder.Services.AddCors(options => options.AddPolicy(MediaCors.Policy, policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET", "HEAD")));

        if (singleUserMode)
        {
            authBuilder
                .AddScheme<AuthenticationSchemeOptions, SingleUserAuthenticationHandler>(
                    AuthConstants.SingleUserScheme,
                    _ => { });
            builder.Services.AddSingleton<IFrostStreamAuthorizer, AllowAllAuthorizer>();
            builder.Services.AddSingleton<IOpenFgaTupleWriter, NullOpenFgaTupleWriter>();
            builder.Services.AddSingleton<IBundleManagementService, NullBundleManagementService>();
            builder.Services.AddSingleton<IDirectoryService, NullDirectoryService>();
        }
        else
        {
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
                                        .GetRequiredService<ILogger<Program>>()
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
            builder.Services.AddHttpClient<AuthentikDirectoryService>();
            builder.Services.AddScoped<IDirectoryService>(sp => sp.GetRequiredService<AuthentikDirectoryService>());
        }

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
        // Per-endpoint policies (fs.endpoint:<id>) are resolved dynamically rather than registered up front.
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, EndpointPolicyProvider>();
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        
        builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
        builder.Services.AddHttpClient<IBackupServiceClient, BackupServiceClient>(client =>
            client.BaseAddress = new Uri(builder.Configuration["BackupService:BaseUrl"] ?? "http://backupservice"));
        builder.Services.AddSingleton<BackupJobService>();
        builder.Services.AddSingleton<DownloadQueueHub>();
        builder.Services.AddHostedService<DownloadQueueHub>(p => p.GetRequiredService<DownloadQueueHub>());
        builder.Services.AddSingleton<RenditionProgressHub>();
        builder.Services.AddHostedService<RenditionProgressHub>(p => p.GetRequiredService<RenditionProgressHub>());
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.Services.AddFrostStreamStorage();

        //var natsUrl = builder.Configuration["NATS:Url"] ?? "nats://localhost:24040";
        builder.Services.AddNats(options =>
        {
            options.Url = builder.Configuration.GetConnectionString("nats")
                          ?? builder.Configuration["NATS:Url"]
                          ?? "nats://localhost:24040";
            options.EnableTopologyProvisioning = false;
        });

        
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        // if (app.Environment.IsDevelopment())
        // {
        var exposeOpenApi = builder.Environment.IsDevelopment() ||
            builder.Configuration.GetValue("Auth:ExposeOpenApi", false);
        if (exposeOpenApi)
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }
        //}

        if (singleUserMode)
        {
            app.Logger.LogWarning("AUTH DISABLED - SINGLE_USER_MODE is active; full access is granted to all requests.");
        }

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth") ||
                context.Request.Path.StartsWithSegments("/api/auth"))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                    return Task.CompletedTask;
                });
            }

            await next();
        });

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api/global/backups"))
            {
                app.Logger.LogError(ex, "BackupService request failed.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "The backup service is unavailable. Check its health and configured backup directory."
                });
            }
        });

        if (Uri.TryCreate(authOptions.PublicOrigin, UriKind.Absolute, out var configuredPublicOrigin) &&
            configuredPublicOrigin.Scheme == Uri.UriSchemeHttps)
        {
            app.UseHttpsRedirection();
        }
        app.UseCors();
        app.UseAuthentication();
        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();

        app.Run();
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
