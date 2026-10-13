using DataBridge.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Deployment;
using Typesense.Setup;
namespace FrostStream.Integrations.Typesense;
/// <summary>Full search provider registration; shared modules depend on provider-neutral contracts.</summary>
public static class TypesenseRegistration
{
    public static IHostApplicationBuilder AddTypesenseIntegration(this IHostApplicationBuilder builder)
    {
        var node = BuildTypesenseNode(builder.Configuration);
        var apiKey = builder.Configuration["Typesense:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Typesense:ApiKey is required for Full search.");
        if (!builder.Services.TryAddModule(typeof(TypesenseRegistration))) return builder;
        builder.Services.AddTypesenseClient(config =>
        {
            config.Nodes = [node];
            config.ApiKey = apiKey;
        });
        builder.Services.AddSingleton<TypesenseGenerationRouter>();
        builder.Services.AddSingleton<ISearchReader, TypesenseSearchReader>();
        builder.Services.AddSingleton<ISearchIndexWriter, TypesenseIndexService>();

        builder.Services.AddSingleton<TypesenseStartupService>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("search collections", 50,
            (sp, ct) => sp.GetRequiredService<TypesenseStartupService>().StartAsync(ct)));
        return builder;
    }
    private static Node BuildTypesenseNode(IConfiguration configuration)
    {
        foreach (var alias in new[] { "ConnectionStrings:typesense", "Typesense:Host", "Typesense:Port", "Typesense:Protocol" })
            if (configuration[alias] is not null)
                throw new InvalidOperationException($"{alias} is obsolete. Configure Typesense:Url.");
        var url = configuration["Typesense:Url"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Typesense:Url must be an absolute HTTP(S) URL.");
        var port = uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var additionalPath = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return new Node(uri.Host, port, uri.Scheme, additionalPath);
    }
}
