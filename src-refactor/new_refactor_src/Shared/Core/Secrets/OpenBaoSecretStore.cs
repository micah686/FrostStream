using Microsoft.Extensions.Options;
using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;

namespace Shared.Secrets;

public sealed class OpenBaoSecretStore : ISecretStore
{
    private readonly IVaultClient _client;
    private readonly string _kvMount;

    public OpenBaoSecretStore(IOptions<OpenBaoOptions> options)
    {
        var opts = options.Value;

        IAuthMethodInfo authMethod;
        if (!string.IsNullOrWhiteSpace(opts.Token))
        {
            authMethod = new TokenAuthMethodInfo(opts.Token);
        }
        else if (!string.IsNullOrWhiteSpace(opts.RoleId) && !string.IsNullOrWhiteSpace(opts.SecretId))
        {
            authMethod = new AppRoleAuthMethodInfo(opts.RoleId, opts.SecretId);
        }
        else
        {
            throw new InvalidOperationException(
                "OpenBAO authentication is not configured. Set OpenBao:Token or OpenBao:RoleId + OpenBao:SecretId.");
        }

        var settings = new VaultClientSettings(opts.Address, authMethod);
        _client = new VaultClient(settings);
        _kvMount = opts.KvMount;
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _client.V1.Secrets.KeyValue.V2
                .ReadSecretAsync(path: path, mountPoint: _kvMount)
                .ConfigureAwait(false);

            if (secret?.Data?.Data is null)
            {
                return null;
            }

            var dict = new Dictionary<string, string>(secret.Data.Data.Count, StringComparer.Ordinal);
            foreach (var kvp in secret.Data.Data)
            {
                dict[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
            }

            return dict;
        }
        catch (VaultApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        var data = values.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value, StringComparer.Ordinal);
        await _client.V1.Secrets.KeyValue.V2
            .WriteSecretAsync(path: path, data: data, mountPoint: _kvMount)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.V1.Secrets.KeyValue.V2
                .DeleteMetadataAsync(path: path, mountPoint: _kvMount)
                .ConfigureAwait(false);
        }
        catch (VaultApiException ex) when (ex.StatusCode == 404)
        {
            // already gone — treat as success
        }
    }
}
