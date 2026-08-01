namespace Conduit.NATS;

public enum StreamRetention
{
    Limits,
    Interest,
    WorkQueue
}

public enum StorageType
{
    File,
    Memory
}

public enum AckPolicy
{
    None,
    All,
    Explicit
}

public enum DeliverPolicy
{
    All,
    Last,
    New,
    ByStartSequence,
    ByStartTime,
    LastPerSubject
}
