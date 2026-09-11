using DataBridge.LiveChat;
using DataBridge.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.LiveChat;

namespace DataBridge.Initialization;

public sealed record InitializationOptions
{
    public const string SectionName = "Initialization";
    public string InitProfile { get; set; } = "frostream-full-init";
    public string? RequiredDirectory { get; set; }
}

public sealed class ApplicationCompatibilityValidator(
    IInitializationStateStore stateStore,
    NpgsqlDataSource dataSource,
    TypesenseInitialization typesense,
    IOptions<LiveChatOptions> liveChatOptions,
    IServiceProvider services,
    IOptions<InitializationOptions> options)
{
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var version = await stateStore.ReadVersionAsync(cancellationToken);
        if (version != ApplicationInitializationCoordinator.CurrentVersion)
        {
            throw Incompatible($"database initialization version is {version?.ToString() ?? "missing"}; required version is {ApplicationInitializationCoordinator.CurrentVersion}");
        }

        await using (var command = dataSource.CreateCommand("SELECT to_regclass('cleipnir.flows') IS NOT NULL"))
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                throw Incompatible("Cleipnir workflow tables are missing");
        }

        try
        {
            await typesense.ValidateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Incompatible(ex.Message, ex);
        }

        if (liveChatOptions.Value.Enabled)
        {
            var schema = services.GetRequiredService<ClickHouseSchemaService>();
            try
            {
                await schema.ValidateSchemaAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Incompatible(ex.Message, ex);
            }
        }

        var directory = options.Value.RequiredDirectory;
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            throw Incompatible($"required persistent directory '{directory}' is missing");
    }

    private InvalidOperationException Incompatible(string reason, Exception? inner = null) => new(
        $"FrostStream runtime state is not initialized or compatible: {reason}. " +
        $"Stop the runtime and run init profile '{options.Value.InitProfile}', then start the runtime again.", inner);
}
