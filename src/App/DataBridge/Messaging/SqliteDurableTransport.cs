using System.Text.Json;
using DataBridge.Persistence.Sqlite;
using DataBridge.Persistence.Workflows;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace DataBridge.Messaging;

/// <summary>Persistent inbox and transactional reply outbox. Handler side effects must be idempotent:
/// a crash before acknowledgement can replay a handler, just as with Full's durable transport.</summary>
public sealed class SqliteDurableTransport : IDurableJobPublisher, IDurableJobConsumer, IWorkerJobRoutes
{
    private readonly SqliteWorkflowDatabase db;
    private readonly ILogger<SqliteDurableTransport> logger;
    private readonly AsyncLocal<Delivery?> current = new();
    private readonly Dictionary<(string, string), string> routes = new();
    private readonly TimeSpan leaseDuration;
    private readonly Lazy<Task> initialization;
    private readonly Shared.Deployment.ApplicationStartupGate? startup;
    private readonly JsonSerializerOptions json = new JsonSerializerOptions(JsonSerializerDefaults.Web).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public SqliteDurableTransport(SqliteConnectionFactory connections, ILogger<SqliteDurableTransport> logger,
        TimeSpan? leaseDuration = null, Shared.Deployment.ApplicationStartupGate? startup = null)
    {
        this.logger = logger;
        this.startup = startup;
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        this.leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
        if (this.leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        db = new(connections);
        initialization = new Lazy<Task>(() => db.Write(s => s.Execute("""
            CREATE TABLE IF NOT EXISTS messaging_messages(
                seq INTEGER PRIMARY KEY AUTOINCREMENT, subject TEXT NOT NULL, message_id TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL, headers TEXT NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS messaging_inbox(
                consumer TEXT NOT NULL, seq INTEGER NOT NULL REFERENCES messaging_messages(seq),
                attempts INTEGER NOT NULL DEFAULT 0, token TEXT, available INTEGER NOT NULL DEFAULT 0,
                acknowledged INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(consumer,seq));
            CREATE INDEX IF NOT EXISTS messaging_inbox_pending ON messaging_inbox(consumer,acknowledged,available);
            """)));
        Conduit.NATS.ITopologySource[] sources = [new DownloadTopology(), new ArtifactStorageTopology(),
            new LocalImportTopology(), new PlaylistTopology(), new BackgroundJobsTopology()];
        foreach (var spec in sources.SelectMany(s => s.GetConsumers()))
            routes[(spec.StreamName.Value, spec.DurableName.Value)] = spec.FilterSubject!;
    }

    public Task EnsureTaggedConsumerAsync(string stream, string consumer, string subject, string tag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (routes) routes[(stream, $"{consumer}-{tag}")] = $"{subject}.{tag}";
        return Task.CompletedTask;
    }

    private record Outgoing(string Subject, string Id, string Payload, string Headers, long Created);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Insert(SqliteWorkflowDatabase.Session s, Outgoing m) => s.Execute(
        "INSERT INTO messaging_messages(subject,message_id,payload,headers,created) VALUES($1,$2,$3,$4,$5) ON CONFLICT(message_id) DO NOTHING",
        m.Subject, m.Id, m.Payload, m.Headers, m.Created);

    public async Task PublishAsync<T>(string subject, T message, string? messageId, MessageHeaders? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        cancellationToken.ThrowIfCancellationRequested();
        await initialization.Value.WaitAsync(cancellationToken);
        var outgoing = new Outgoing(subject, messageId, JsonSerializer.Serialize(message, json),
            JsonSerializer.Serialize(headers?.Headers ?? new(), json), Now);
        if (current.Value is { } delivery)
        {
            lock (delivery.Outbox)
            {
                if (delivery.Completed) throw new InvalidOperationException("Publish must precede acknowledgement.");
                delivery.Outbox.Add(outgoing);
            }
        }
        else await db.Write(s => { Insert(s, outgoing); return true; });
    }

    public async Task PublishBatchAsync<T>(IReadOnlyList<BatchMessage<T>> messages, CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            try { await PublishAsync(m.Subject, m.Message, m.MessageId, m.Headers, cancellationToken); }
            catch (Exception e) { failures.Add(new InvalidOperationException($"Message {i} ({m.MessageId}) failed", e)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    public Task ConsumePullAsync<T>(StreamName stream, ConsumerName consumer, Func<IDurableMessageContext<T>, Task> handler,
        DurableConsumeOptions? options = null, CancellationToken cancellationToken = default)
    {
        string filter;
        lock (routes) filter = routes.TryGetValue((stream.Value, consumer.Value), out var route) ? route
            : throw new ArgumentException($"Unknown durable route {stream.Value}/{consumer.Value}");
        return ConsumeAsync(stream, SubjectName.From(filter), handler, (options ?? new()) with { DurableName = consumer }, cancellationToken);
    }

    public Task ConsumeAsync<T>(StreamName stream, SubjectName subject, Func<IDurableMessageContext<T>, Task> handler,
        DurableConsumeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var concurrency = options.MaxConcurrency ?? 1;
        if (concurrency <= 0 || options.BatchSize <= 0 || options.MaxAckPending is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        concurrency = Math.Min(concurrency, Math.Min(options.BatchSize, options.MaxAckPending ?? int.MaxValue));
        var identity = stream.Value + "/" + (options.DurableName?.Value ?? subject.Value);
        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Run(identity, subject.Value, handler, cancellationToken)));
    }

    private static bool Matches(string filter, string subject)
    {
        var f = filter.Split('.'); var s = subject.Split('.');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == ">") return i < s.Length;
            if (i >= s.Length || (f[i] != "*" && f[i] != s[i])) return false;
        }
        return f.Length == s.Length;
    }

    private async Task Run<T>(string consumer, string filter, Func<IDurableMessageContext<T>, Task> handler, CancellationToken ct)
    {
        if (startup is not null) await startup.WaitAsync(ct);
        await initialization.Value.WaitAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            var delivery = await db.Write(s =>
            {
                var candidates = s.Query("""
                    SELECT m.seq,m.subject,m.payload,m.headers,m.created,COALESCE(i.attempts,0)
                    FROM messaging_messages m LEFT JOIN messaging_inbox i ON i.seq=m.seq AND i.consumer=$1
                    WHERE COALESCE(i.acknowledged,0)=0 AND COALESCE(i.available,0)<=$2 ORDER BY m.seq
                    """, r => new Delivery(this, consumer, r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt32(5) + 1), consumer, Now);
                var item = candidates.FirstOrDefault(m => Matches(filter, m.Subject));
                if (item is null) return null;
                s.Execute("""
                    INSERT INTO messaging_inbox(consumer,seq,attempts,token,available) VALUES($1,$2,$3,$4,$5)
                    ON CONFLICT(consumer,seq) DO UPDATE SET attempts=$3,token=$4,available=$5
                    """, consumer, item.Seq, item.Attempts, item.Token, Now + (long)leaseDuration.TotalMilliseconds);
                return item;
            });
            if (delivery is null) { await Task.Delay(100, ct); continue; }
            current.Value = delivery;
            try
            {
                await handler(new Context<T>(delivery, JsonSerializer.Deserialize<T>(delivery.Payload, json)!));
            }
            catch (Exception e) { logger.LogError(e, "Durable delivery failed for {Subject}", delivery.Subject); }
            finally
            {
                current.Value = null;
                // Unacknowledged handlers retry; cancellation releases the lease for another consumer.
                if (!delivery.Completed)
                {
                    try { await delivery.Finish(false, TimeSpan.FromSeconds(1), CancellationToken.None); }
                    catch (InvalidOperationException) { /* A newer lease owns the retry. */ }
                }
            }
        }
    }

    private sealed class Delivery(SqliteDurableTransport owner, string consumer, long seq, string subject,
        string payload, string headers, long created, int attempts)
    {
        public long Seq => seq;
        public string Subject => subject;
        public string Payload => payload;
        public string Headers => headers;
        public long Created => created;
        public int Attempts => attempts;
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public List<Outgoing> Outbox { get; } = [];
        public bool Completed { get; private set; }
        public async Task Finish(bool ack, TimeSpan delay, CancellationToken ct, bool commitOutbox = true)
        {
            ct.ThrowIfCancellationRequested();
            await owner.db.Write(s =>
            {
                if (Completed) return false;
                var updated = s.Execute("""
                    UPDATE messaging_inbox SET acknowledged=$1,available=$2,token=NULL
                    WHERE consumer=$3 AND seq=$4 AND token=$5 AND available>$6
                    """, ack ? 1 : 0, Now + (long)delay.TotalMilliseconds, consumer, seq, Token, Now);
                if (updated == 0) throw new InvalidOperationException("Delivery lease expired or was replaced.");
                if (ack && commitOutbox) lock (Outbox) foreach (var m in Outbox) Insert(s, m);
                return true;
            });
            Completed = true;
        }
        public async Task Renew(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var changed = await owner.db.Write(s => s.Execute("""
                UPDATE messaging_inbox SET available=$1 WHERE consumer=$2 AND seq=$3 AND token=$4 AND available>$5
                """, Now + (long)owner.leaseDuration.TotalMilliseconds, consumer, seq, Token, Now));
            if (changed == 0) throw new InvalidOperationException("Delivery lease expired or was replaced.");
        }
    }

    private sealed class Context<T>(Delivery d, T message) : IDurableMessageContext<T>
    {
        public T Message => message;
        public string Subject => d.Subject;
        public MessageHeaders Headers => new(JsonSerializer.Deserialize<Dictionary<string, string>>(d.Headers)!);
        public string? ReplyTo => null;
        public ulong Sequence => (ulong)d.Seq;
        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(d.Created);
        public bool Redelivered => d.Attempts > 1;
        public uint NumDelivered => (uint)d.Attempts;
        public Task AckAsync(CancellationToken cancellationToken = default) => d.Finish(true, TimeSpan.Zero, cancellationToken);
        public Task TermAsync(CancellationToken cancellationToken = default) => d.Finish(true, TimeSpan.Zero, cancellationToken, commitOutbox: false);
        public Task NackAsync(TimeSpan? delay = null, CancellationToken cancellationToken = default) => d.Finish(false, delay ?? TimeSpan.FromSeconds(1), cancellationToken);
        public Task InProgressAsync(CancellationToken cancellationToken = default) => d.Renew(cancellationToken);
        public Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException("Durable jobs have no request reply address.");
    }
}
