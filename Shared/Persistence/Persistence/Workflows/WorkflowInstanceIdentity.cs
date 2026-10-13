namespace DataBridge.Persistence.Workflows;
public static class WorkflowInstanceIdentity
{
    public static bool TryParseJobInstance(string instance, out Guid jobId, out Guid runId)
    {
        jobId = default;
        runId = default;
        return instance.Length == 65
               && instance[32] == '-'
               && Guid.TryParseExact(instance[..32], "N", out jobId)
               && Guid.TryParseExact(instance[33..], "N", out runId);
    }

    public static bool TryParseGroupInstance(string instance, out Guid groupId)
    {
        groupId = default;
        return instance.Length == 32 && Guid.TryParseExact(instance, "N", out groupId);
    }

    public static bool TryParseImportInstance(string instance, out Guid itemId)
    {
        itemId = default;
        var separator = instance.IndexOf("/attempt-", StringComparison.Ordinal);
        return separator == 32 && instance.Length > 41
               && instance[41..].All(c => c is >= '0' and <= '9')
               && Guid.TryParseExact(instance[..32], "N", out itemId);
    }
}
