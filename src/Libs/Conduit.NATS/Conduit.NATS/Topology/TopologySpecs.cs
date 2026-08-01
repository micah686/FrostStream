namespace Conduit.NATS;

/// <summary>
/// Declarative specification for a JetStream stream.
/// </summary>
public class StreamSpec
{
    public StreamName Name { get; set; } = StreamName.From("default");
    public string[] Subjects { get; set; } = Array.Empty<string>();
    public long MaxBytes { get; set; } = -1;
    public long MaxMsgSize { get; set; } = -1;
    public TimeSpan MaxAge { get; set; } = TimeSpan.Zero; // 0 = unlimited
    public int Replicas { get; set; } = 1;
    public StorageType StorageType { get; set; } = StorageType.File;
    public StreamRetention RetentionPolicy { get; set; } = StreamRetention.Limits;
}

/// <summary>
/// Declarative specification for a durable JetStream consumer.
/// </summary>
public class ConsumerSpec
{
    public StreamName StreamName { get; set; } = StreamName.From("default");
    public ConsumerName DurableName { get; set; } = ConsumerName.From("default");
    public QueueGroup? DeliverGroup { get; set; }
    public string? Description { get; set; }

    private string? _filterSubject;
    private List<string> _filterSubjects = new();

    public string? FilterSubject
    {
        get => _filterSubject;
        set => _filterSubject = value;
    }

    public List<string> FilterSubjects
    {
        get => _filterSubjects;
        set => _filterSubjects = value;
    }

    public IReadOnlyList<string> GetFilterSubjects()
    {
        if (_filterSubjects.Count > 0)
            return _filterSubjects;
        if (!string.IsNullOrEmpty(_filterSubject))
            return new[] { _filterSubject };
        return Array.Empty<string>();
    }

    public DeliverPolicy DeliverPolicy { get; set; } = DeliverPolicy.All;
    public AckPolicy AckPolicy { get; set; } = AckPolicy.Explicit;
    public TimeSpan AckWait { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxDeliver { get; set; } = -1;
    public int? MaxAckPending { get; set; }
    public TimeSpan[]? Backoff { get; set; }
}

/// <summary>
/// Specification for a NATS KV bucket to be provisioned.
/// </summary>
public class BucketSpec
{
    public BucketName Name { get; set; } = BucketName.From("default");
    public StorageType StorageType { get; set; } = StorageType.File;
    public TimeSpan MaxAge { get; set; } = TimeSpan.Zero;
    public long MaxBytes { get; set; } = -1;
    public int History { get; set; } = 1;
    public int Replicas { get; set; } = 1;
    public string? Description { get; set; }
}

/// <summary>
/// Specification for a NATS Object Store bucket to be provisioned.
/// </summary>
public class ObjectStoreSpec
{
    public BucketName Name { get; set; } = BucketName.From("default");
    public StorageType StorageType { get; set; } = StorageType.File;
    public TimeSpan MaxAge { get; set; } = TimeSpan.Zero;
    public long MaxBytes { get; set; } = -1;
    public int Replicas { get; set; } = 1;
    public string? Description { get; set; }
}
