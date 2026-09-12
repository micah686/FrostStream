using Microsoft.Extensions.Hosting;

namespace FrostStream.Lite;

public static class LiteProductionValidation
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsProduction())
            return;

        var missing = new List<string>();
        Require(configuration.GetConnectionString("froststreamdb"), "ConnectionStrings:froststreamdb", missing);
        Require(configuration["Typesense:Url"] ?? configuration.GetConnectionString("typesense"), "Typesense:Url", missing);
        Require(configuration["Typesense:ApiKey"], "Typesense:ApiKey", missing);
        Require(configuration["FROSTSTREAM_STORAGE_ROOT"], "FROSTSTREAM_STORAGE_ROOT", missing);

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"FrostStream Lite production configuration is missing: {string.Join(", ", missing)}.");
        }
    }

    private static void Require(string? value, string name, ICollection<string> missing)
    {
        if (string.IsNullOrWhiteSpace(value))
            missing.Add(name);
    }
}
