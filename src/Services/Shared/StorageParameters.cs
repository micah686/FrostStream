using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Shared;

public abstract class StorageParametersBase : IValidatableObject
{
    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return [];
    }
}

public sealed class PosixLocalStorageParameters : StorageParametersBase
{
    [Required]
    [MinLength(1)]
    public required string Path { get; init; }
}

public enum StreamingProtocol
{
    Ftp,
    Ftps,
    Sftp
}

public sealed class StreamingNetworkStorageParameters : StorageParametersBase
{
    [Required]
    public StreamingProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Host { get; init; }

    [Range(1, 65535)]
    public int? Port { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? PublicKey { get; init; }
    public string? BasePath { get; init; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasUsername = !string.IsNullOrWhiteSpace(Username);
        var hasPassword = !string.IsNullOrWhiteSpace(Password);
        var hasPrivateKey = !string.IsNullOrWhiteSpace(PrivateKey);

        if (!hasUsername && (hasPassword || hasPrivateKey))
        {
            yield return new ValidationResult(
                "Username is required when password or privateKey is provided.",
                [nameof(Username), nameof(Password), nameof(PrivateKey)]);
        }

        if (hasUsername && !hasPassword && !hasPrivateKey)
        {
            yield return new ValidationResult(
                "Provide either password or privateKey when username is set.",
                [nameof(Username), nameof(Password), nameof(PrivateKey)]);
        }

        if (hasPassword && hasPrivateKey)
        {
            yield return new ValidationResult(
                "Use either password-based auth or privateKey auth, not both.",
                [nameof(Password), nameof(PrivateKey)]);
        }
    }
}

public enum ObjectStorageProvider
{
    AwsS3,
    AzureBlob,
    GoogleCloudStorage,
    MinIo,
    Other
}

public sealed class ObjectStorageParameters : StorageParametersBase
{
    [Required]
    public ObjectStorageProvider Provider { get; init; }

    [Required]
    [MinLength(1)]
    public required string Container { get; init; }

    public string? Region { get; init; }
    public string? Endpoint { get; init; }
    public string? BasePath { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretKey { get; init; }
    public bool UseDefaultCredentials { get; init; } = true;

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasAccessKeyId = !string.IsNullOrWhiteSpace(AccessKeyId);
        var hasSecretKey = !string.IsNullOrWhiteSpace(SecretKey);

        if (hasAccessKeyId != hasSecretKey)
        {
            yield return new ValidationResult(
                "accessKeyId and secretKey must be provided together.",
                [nameof(AccessKeyId), nameof(SecretKey)]);
        }

        if ((Provider is ObjectStorageProvider.AwsS3 or ObjectStorageProvider.MinIo) &&
            string.IsNullOrWhiteSpace(Region))
        {
            yield return new ValidationResult(
                "region is required for AwsS3 and MinIo.",
                [nameof(Region)]);
        }

        if (!UseDefaultCredentials && !hasAccessKeyId)
        {
            yield return new ValidationResult(
                "Set accessKeyId/secretKey or enable useDefaultCredentials.",
                [nameof(UseDefaultCredentials), nameof(AccessKeyId), nameof(SecretKey)]);
        }
    }
}

public static class StorageParametersSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryDeserialize(
        StorageMethod method,
        string json,
        out StorageParametersBase? parameters,
        out string? error)
    {
        parameters = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Parameters JSON cannot be empty.";
            return false;
        }

        try
        {
            parameters = method switch
            {
                StorageMethod.PosixLocal => JsonSerializer.Deserialize<PosixLocalStorageParameters>(json, JsonOptions),
                StorageMethod.StreamingNetwork => JsonSerializer.Deserialize<StreamingNetworkStorageParameters>(json, JsonOptions),
                StorageMethod.ObjectStorage => JsonSerializer.Deserialize<ObjectStorageParameters>(json, JsonOptions),
                _ => null
            };

            if (parameters is null)
            {
                error = $"Unable to parse parameters for method '{method}'.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid parameters JSON: {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyList<string> Validate(StorageMethod method, string json)
    {
        if (!TryDeserialize(method, json, out var parsed, out var parseError))
        {
            return [parseError ?? "Invalid parameters JSON."];
        }

        var results = new List<ValidationResult>();
        var validationContext = new ValidationContext(parsed!);
        Validator.TryValidateObject(parsed!, validationContext, results, validateAllProperties: true);

        return results.Select(x => x.ErrorMessage ?? "Invalid parameters value.").ToArray();
    }
}
