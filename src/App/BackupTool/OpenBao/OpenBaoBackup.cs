using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BackupTool;

internal sealed record OpenBaoKvExport
{
    public required string KvMount { get; init; }
    public required DateTimeOffset ExportedAtUtc { get; init; }
    public required IReadOnlyList<OpenBaoSecret> Secrets { get; init; }
}

internal sealed record OpenBaoSecret(string Path, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Exports and restores OpenBao (Vault-compatible) KV v2 secrets over the HTTP API.
/// </summary>
internal sealed class OpenBaoBackup
{
    public async Task ExportToFileAsync(OpenBaoOptions options, string outputFile)
    {
        var export = await ExportAsync(options);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputFile)!);
        await File.WriteAllTextAsync(outputFile, JsonSerializer.Serialize(export, BackupJson.Options));
    }

    public async Task<OpenBaoKvExport> ExportAsync(OpenBaoOptions options)
    {
        using var client = NewClient(options);
        var secrets = new List<OpenBaoSecret>();
        await ExportPathAsync(client, options.KvMount, "", secrets);
        return new OpenBaoKvExport
        {
            KvMount = options.KvMount,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Secrets = secrets.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray()
        };
    }

    public async Task RestoreAsync(OpenBaoOptions options, OpenBaoKvExport export)
    {
        if (!string.Equals(export.KvMount, options.KvMount, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"OpenBao export mount '{export.KvMount}' does not match target mount '{options.KvMount}'.");
        }

        using var client = NewClient(options);
        foreach (var secret in export.Secrets)
        {
            using var response = await client.PostAsync(
                $"/v1/{options.KvMount}/data/{secret.Path}",
                new StringContent(JsonSerializer.Serialize(new { data = secret.Values }), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task ExportPathAsync(
        HttpClient client,
        string mount,
        string path,
        List<OpenBaoSecret> secrets)
    {
        using var listRequest = new HttpRequestMessage(HttpMethod.Parse("LIST"), $"/v1/{mount}/metadata/{path}");
        using var listResponse = await client.SendAsync(listRequest);
        if (listResponse.StatusCode == HttpStatusCode.NotFound)
            return;
        listResponse.EnsureSuccessStatusCode();

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var keys = listDoc.RootElement.GetProperty("data").GetProperty("keys").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();

        foreach (var key in keys)
        {
            if (key.EndsWith('/'))
            {
                await ExportPathAsync(client, mount, path + key, secrets);
                continue;
            }

            var secretPath = path + key;
            using var readResponse = await client.GetAsync($"/v1/{mount}/data/{secretPath}");
            readResponse.EnsureSuccessStatusCode();
            using var readDoc = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in readDoc.RootElement.GetProperty("data").GetProperty("data").EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }

            secrets.Add(new OpenBaoSecret(secretPath, values));
        }
    }

    private static HttpClient NewClient(OpenBaoOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
            throw new InvalidOperationException("OpenBao token is required for backup/restore.");

        var client = new HttpClient { BaseAddress = new Uri(options.Address, UriKind.Absolute) };
        client.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
