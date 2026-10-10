using System.Text.RegularExpressions;
using Vogen;

namespace Conduit.NATS;

[ValueObject<string>]
public partial struct StreamName
{
    private static readonly Regex ValidPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Validation.Invalid("Stream name cannot be empty");
        if (value.Length > 256)
            return Validation.Invalid("Stream name cannot exceed 256 characters");
        if (!ValidPattern.IsMatch(value))
            return Validation.Invalid("Stream name can only contain letters, numbers, underscores, and hyphens");
        return Validation.Ok;
    }
}

[ValueObject<string>]
public partial struct ConsumerName
{
    private static readonly Regex ValidPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Validation.Invalid("Consumer name cannot be empty");
        if (value.Length > 256)
            return Validation.Invalid("Consumer name cannot exceed 256 characters");
        if (!ValidPattern.IsMatch(value))
            return Validation.Invalid("Consumer name can only contain letters, numbers, underscores, and hyphens");
        return Validation.Ok;
    }
}

[ValueObject<string>]
public partial struct SubjectName
{
    // NATS subjects allow alphanumeric, dots (hierarchy), and wildcards (* single token, > multi).
    private static readonly Regex ValidPattern = new("^[a-zA-Z0-9_\\-.*>]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Validation.Invalid("Subject name cannot be empty");
        if (value.Length > 1024)
            return Validation.Invalid("Subject name cannot exceed 1024 characters");

        var tokens = value.Split('.');
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token))
                return Validation.Invalid("Subject cannot have empty tokens");
            if (token.Contains('*') && token != "*")
                return Validation.Invalid("Single-level wildcard (*) must be a complete token");
            if (token.Contains('>') && token != ">")
                return Validation.Invalid("Multi-level wildcard (>) must be a complete token");
        }

        if (value.Contains("..") || value.Contains("**") || value.Contains(">>"))
            return Validation.Invalid("Subject contains invalid pattern sequences");
        if (!ValidPattern.IsMatch(value))
            return Validation.Invalid("Subject name contains invalid characters");
        if (value.Contains('>') && !value.EndsWith('>'))
            return Validation.Invalid("Multi-level wildcard (>) must be at the end of subject");

        return Validation.Ok;
    }
}

[ValueObject<string>]
public partial struct BucketName
{
    private static readonly Regex ValidPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Validation.Invalid("Bucket name cannot be empty");
        if (value.Length > 256)
            return Validation.Invalid("Bucket name cannot exceed 256 characters");
        if (!ValidPattern.IsMatch(value))
            return Validation.Invalid("Bucket name can only contain letters, numbers, underscores, and hyphens");
        return Validation.Ok;
    }
}

[ValueObject<string>]
public partial struct QueueGroup
{
    private static readonly Regex ValidPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Validation.Invalid("Queue group cannot be empty");
        if (value.Length > 256)
            return Validation.Invalid("Queue group cannot exceed 256 characters");
        if (!ValidPattern.IsMatch(value))
            return Validation.Invalid("Queue group can only contain letters, numbers, underscores, and hyphens");
        return Validation.Ok;
    }
}
