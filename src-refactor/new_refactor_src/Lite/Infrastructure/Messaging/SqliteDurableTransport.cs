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
    private readonly LocalQueueOptions settings;
    private readonly SemaphoreSlim executionSlots;
    private readonly object wakeLock = new();
    private readonly Dictionary<string, (string Filter, TaskCompletionSource Wake)> wakeups = new();
    private long probes, claims;
    private long retentionCursor;
    private string dedupCursor = "";
    public long ReadProbes => Interlocked.Read(ref probes);
    public long WriteClaims => Interlocked.Read(ref claims);
    private void SignalConsumer(string consumer)
    {
        lock (wakeLock) if (wakeups.TryGetValue(consumer, out var entry))
            {
                wakeups[consumer] = (entry.Filter, new(TaskCreationOptions.RunContinuationsAsynchronously)); entry.Wake.TrySetResult();
            }
    }
    private void SignalSubject(string subject)
    {
        lock (wakeLock) foreach (var consumer in wakeups.Where(entry => Matches(entry.Value.Filter, subject)).Select(entry => entry.Key).ToArray())
                SignalConsumer(consumer);
    }
    private Task WakeTask(string consumer, string filter)
    {
        lock (wakeLock)
        {
            if (!wakeups.TryGetValue(consumer, out var entry)) wakeups[consumer] = entry = (filter, new(TaskCreationOptions.RunContinuationsAsynchronously));
            return entry.Wake.Task;
        }
    }
    private readonly Lazy<Task> initialization;
    private readonly Shared.Deployment.ApplicationStartupGate? startup;
    private readonly JsonSerializerOptions json = new JsonSerializerOptions(JsonSerializerDefaults.Web).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public SqliteDurableTransport(SqliteConnectionFactory connections, ILogger<SqliteDurableTransport> logger,
        TimeSpan? leaseDuration = null, Shared.Deployment.ApplicationStartupGate? startup = null, LocalQueueOptions? settings = null)
    {
        this.logger = logger;
        this.settings = settings ?? new();
        this.settings.Validate();
        executionSlots = new(this.settings.MaxConcurrentHandlers);
        this.startup = startup;
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        this.leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
        if (this.leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        db = new(connections);
        initialization = new Lazy<Task>(InitializeCoreAsync);
        foreach (var route in ApplicationJobRoutes.All)
            routes[(route.Stream, route.Consumer)] = route.Subject;
    }

    public Task InitializeAsync(CancellationToken ct = default) => initialization.Value.WaitAsync(ct);

    private async Task InitializeCoreAsync()
    {
        await db.Write(s => s.Execute("""
            CREATE TABLE IF NOT EXISTS messaging_messages(
                seq INTEGER PRIMARY KEY AUTOINCREMENT, subject TEXT NOT NULL, message_id TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL, headers TEXT NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS messaging_inbox(
                consumer TEXT NOT NULL, seq INTEGER NOT NULL REFERENCES messaging_messages(seq),
                attempts INTEGER NOT NULL DEFAULT 0, token TEXT, available INTEGER NOT NULL DEFAULT 0,
                acknowledged INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(consumer,seq));
            DROP INDEX IF EXISTS messaging_inbox_pending;
            CREATE INDEX IF NOT EXISTS messaging_inbox_claim ON messaging_inbox(consumer,acknowledged,available,seq);
            CREATE TABLE IF NOT EXISTS messaging_routes(consumer TEXT PRIMARY KEY, filter TEXT NOT NULL, cursor INTEGER NOT NULL DEFAULT 0, ready INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS messaging_dedup(message_id TEXT PRIMARY KEY, created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS messaging_dedup_created ON messaging_dedup(created);
            CREATE TABLE IF NOT EXISTS messaging_failures(consumer TEXT NOT NULL, seq INTEGER NOT NULL,
                error TEXT NOT NULL, failed_at INTEGER NOT NULL, PRIMARY KEY(consumer,seq));
            CREATE INDEX IF NOT EXISTS messaging_failures_time ON messaging_failures(failed_at DESC);
            CREATE INDEX IF NOT EXISTS messaging_inbox_sequence ON messaging_inbox(seq,acknowledged);
            """));
        long cursor = 0;
        while (true)
        {
            var page = await db.Write(s =>
            {
                var messages = s.Query("SELECT seq,message_id,created FROM messaging_messages WHERE seq>$1 ORDER BY seq LIMIT 128", r => (Seq: r.GetInt64(0), Id: r.GetString(1), Created: r.GetInt64(2)), cursor);
                foreach (var message in messages) s.Execute("INSERT OR IGNORE INTO messaging_dedup(message_id,created) VALUES($1,$2)", message.Id, message.Created);
                return messages;
            });
            if (page.Count == 0) break;
            cursor = page[^1].Seq; await Task.Yield();
        }
        foreach (var route in ApplicationJobRoutes.All)
            await RegisterRoute(route.Stream + "/" + route.Consumer, route.Subject);
        foreach (var route in await db.Read(s => s.Query("SELECT consumer,filter FROM messaging_routes WHERE ready=0", r => (Consumer: r.GetString(0), Filter: r.GetString(1)))))
            await RegisterRoute(route.Consumer, route.Filter);
    }

    private async Task RegisterRoute(string consumer, string filter)
    {
        await db.Write(s =>
        {
            var existing = s.Scalar("SELECT filter FROM messaging_routes WHERE consumer=$1", consumer);
            if (existing is not null && (string)existing != filter) throw new InvalidOperationException("A durable consumer cannot change its filter.");
            s.Execute("INSERT OR IGNORE INTO messaging_routes(consumer,filter) VALUES($1,$2)", consumer, filter);
            return s.Execute("UPDATE messaging_routes SET ready=0 WHERE consumer=$1", consumer);
        });
        // Bounded backfill also supports consumers registered after publication and restarts.
        while (await db.Write(s =>
        {
            var cursor = Convert.ToInt64(s.Scalar("SELECT cursor FROM messaging_routes WHERE consumer=$1", consumer));
            var page = s.Query("SELECT seq,subject FROM messaging_messages WHERE seq>$1 ORDER BY seq LIMIT 128",
                r => (Seq: r.GetInt64(0), Subject: r.GetString(1)), cursor);
            foreach (var item in page)
                if (Matches(filter, item.Subject)) s.Execute("INSERT OR IGNORE INTO messaging_inbox(consumer,seq) VALUES($1,$2)", consumer, item.Seq);
            if (page.Count > 0) s.Execute("UPDATE messaging_routes SET cursor=$1 WHERE consumer=$2", page[^1].Seq, consumer);
            return page.Count == 128;
        })) await Task.Yield();
        await db.Write(s => s.Execute("UPDATE messaging_routes SET ready=1 WHERE consumer=$1", consumer));
        SignalConsumer(consumer);
    }

    public async Task EnsureTaggedConsumerAsync(string stream, string consumer, string subject, string tag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (routes) routes[(stream, $"{consumer}-{tag}")] = $"{subject}.{tag}";
        await initialization.Value.WaitAsync(cancellationToken);
        await RegisterRoute(stream + "/" + consumer + "-" + tag, subject + "." + tag);
    }

    private record Outgoing(string Subject, string Id, string Payload, string Headers, long Created);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Insert(SqliteWorkflowDatabase.Session s, Outgoing m)
    {
        if (s.Execute("INSERT OR IGNORE INTO messaging_dedup(message_id,created) VALUES($1,$2)", m.Id, m.Created) == 0)
        {
            s.Execute("UPDATE messaging_dedup SET created=MAX(created,$1) WHERE message_id=$2", m.Created, m.Id); return;
        }
        s.Execute("INSERT INTO messaging_messages(subject,message_id,payload,headers,created) VALUES($1,$2,$3,$4,$5)", m.Subject, m.Id, m.Payload, m.Headers, m.Created);
        var seq = Convert.ToInt64(s.Scalar("SELECT last_insert_rowid()"));
        foreach (var route in s.Query("SELECT consumer,filter FROM messaging_routes", r => (Consumer: r.GetString(0), Filter: r.GetString(1))))
            if (Matches(route.Filter, m.Subject)) s.Execute("INSERT INTO messaging_inbox(consumer,seq) VALUES($1,$2)", route.Consumer, seq);
    }

    public async Task<int> MaintainAsync(CancellationToken ct = default)
    {
        await initialization.Value.WaitAsync(ct);
        return await db.Write(s =>
        {
            // Backfill declares new required deliveries before retention may remove shared payloads.
            if (s.Scalar("SELECT 1 FROM messaging_routes WHERE ready=0 LIMIT 1") is not null) return 0;
            // Cursor pages bound discovery too: a LIMIT over eligible rows could scan millions of active rows.
            var page = s.Query("SELECT seq,created FROM messaging_messages WHERE seq>$1 ORDER BY seq LIMIT 128", r => (Seq: r.GetInt64(0), Created: r.GetInt64(1)), retentionCursor);
            var removed = 0;
            foreach (var item in page)
            {
                if (item.Created >= Now - (long)settings.CompletedRetention.TotalMilliseconds) continue;
                if (s.Scalar("SELECT 1 FROM messaging_inbox WHERE seq=$1 LIMIT 1", item.Seq) is null) continue;
                if (s.Scalar("SELECT 1 FROM messaging_inbox WHERE seq=$1 AND acknowledged<>1 LIMIT 1", item.Seq) is not null) continue;
                s.Execute("DELETE FROM messaging_inbox WHERE seq=$1", item.Seq);
                s.Execute("DELETE FROM messaging_messages WHERE seq=$1", item.Seq); removed++;
            }
            retentionCursor = page.Count == 128 ? page[^1].Seq : 0;
            var markers = s.Query("SELECT message_id,created FROM messaging_dedup WHERE message_id>$1 ORDER BY message_id LIMIT 128", r => (Id: r.GetString(0), Created: r.GetInt64(1)), dedupCursor);
            foreach (var marker in markers)
            {
                if (marker.Created >= Now - (long)settings.DedupRetention.TotalMilliseconds) continue;
                if (s.Scalar("SELECT 1 FROM messaging_messages WHERE message_id=$1", marker.Id) is not null) continue;
                s.Execute("DELETE FROM messaging_dedup WHERE message_id=$1", marker.Id);
            }
            dedupCursor = markers.Count == 128 ? markers[^1].Id : "";
            return removed;
        });
    }
    public async Task<IReadOnlyList<LocalQueueFailure>> GetFailuresAsync(CancellationToken ct = default)
    {
        await initialization.Value.WaitAsync(ct);
        return await db.Read(s => s.Query("SELECT consumer,seq,error,failed_at FROM messaging_failures ORDER BY failed_at DESC LIMIT 100", r => new LocalQueueFailure(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3))));
    }
    public async Task RetryAsync(string consumer, long sequence, CancellationToken ct = default)
    {
        await initialization.Value.WaitAsync(ct);
        await db.Write(s =>
        {
            s.Execute("DELETE FROM messaging_failures WHERE consumer=$1 AND seq=$2", consumer, sequence);
            return s.Execute("UPDATE messaging_inbox SET acknowledged=0,attempts=0,available=0,token=NULL WHERE consumer=$1 AND seq=$2 AND acknowledged=2", consumer, sequence);
        }); SignalConsumer(consumer);
    }

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
                // Resumable workflows can retain an ExecutionContext after the ingress handler
                // acknowledges. Their later publications are independent durable writes.
                if (!delivery.Completed)
                {
                    delivery.Outbox.Add(outgoing);
                    return;
                }
            }
        }
        await db.Write(s => { Insert(s, outgoing); return true; });
        SignalSubject(subject);
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
        concurrency = Math.Min(settings.MaxConsumerConcurrency, Math.Min(concurrency, Math.Min(options.BatchSize, options.MaxAckPending ?? int.MaxValue)));
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
        await RegisterRoute(consumer, filter);
        var idle = TimeSpan.FromMilliseconds(25);
        while (!ct.IsCancellationRequested)
        {
            var wakeTask = WakeTask(consumer, filter);
            Interlocked.Increment(ref probes);
            // A read-only indexed probe avoids taking SQLite's writer lock while idle.
            var candidate = await db.Read(s => s.Scalar("SELECT seq FROM messaging_inbox WHERE consumer=$1 AND acknowledged=0 AND available<=$2 ORDER BY available,seq LIMIT 1", consumer, Now));
            Delivery? delivery = null;
            if (candidate is not null)
            {
                await executionSlots.WaitAsync(ct);
                try
                {
                    delivery = await db.Write(s =>
                    {
                        var items = s.Query("""
                    SELECT m.seq,m.subject,m.payload,m.headers,m.created,i.attempts
                    FROM messaging_inbox i JOIN messaging_messages m ON m.seq=i.seq
                    WHERE i.consumer=$1 AND i.seq=$2 AND i.acknowledged=0 AND i.available<=$3 LIMIT 1
                    """, r => new Delivery(this, consumer, r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt32(5) + 1), consumer, candidate, Now);
                        if (items.Count == 0) return null;
                        var item = items[0];
                        s.Execute("UPDATE messaging_inbox SET attempts=$1,token=$2,available=$3 WHERE consumer=$4 AND seq=$5", item.Attempts, item.Token, Now + (long)leaseDuration.TotalMilliseconds, consumer, item.Seq);
                        Interlocked.Increment(ref claims);
                        return item;
                    });
                }
                catch { executionSlots.Release(); throw; }
            }
            if (delivery is null)
            {
                if (candidate is not null) executionSlots.Release();
                try { await wakeTask.WaitAsync(idle, ct); } catch (TimeoutException) { }
                idle = TimeSpan.FromMilliseconds(Math.Min(settings.MaxIdleDelay.TotalMilliseconds, idle.TotalMilliseconds * 2));
                continue;
            }
            idle = TimeSpan.FromMilliseconds(25);
            current.Value = delivery;
            Exception? failure = null;
            try
            {
                await handler(new Context<T>(delivery, JsonSerializer.Deserialize<T>(delivery.Payload, json)!));
            }
            catch (Exception e) { failure = e; logger.LogError(e, "Durable delivery failed for {Subject}", delivery.Subject); }
            finally
            {
                current.Value = null;
                executionSlots.Release();
                // Unacknowledged handlers retry; cancellation releases the lease for another consumer.
                if (!delivery.Completed)
                {
                    try { await delivery.Finish(false, ct.IsCancellationRequested ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, delivery.Attempts - 1)))), CancellationToken.None, error: failure?.Message ?? "Handler returned without acknowledgment", terminalEligible: !ct.IsCancellationRequested); }
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
        public Task Finish(bool ack, TimeSpan delay, CancellationToken ct, bool commitOutbox = true, string? error = null, bool terminalEligible = true)
        {
            ct.ThrowIfCancellationRequested();
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(delay));
            // Write uses a synchronous, short SQLite transaction. Keep publication and completion
            // under the same lock so a concurrent workflow cannot append after the outbox commits.
            lock (Outbox)
            {
                if (Completed) return Task.CompletedTask;
                owner.db.Write(s =>
                {
                    var terminal = !ack && terminalEligible && Attempts >= owner.settings.MaxAttempts;
                    var updated = s.Execute("""
                        UPDATE messaging_inbox SET acknowledged=$1,available=$2,token=NULL,attempts=CASE WHEN $7=1 THEN attempts ELSE MAX(0,attempts-1) END
                        WHERE consumer=$3 AND seq=$4 AND token=$5 AND available>$6
                        """, ack ? 1 : terminal ? 2 : 0, Now + (long)delay.TotalMilliseconds, consumer, seq, Token, Now, terminalEligible ? 1 : 0);
                    if (updated == 0) throw new InvalidOperationException("Delivery lease expired or was replaced.");
                    if (ack || terminal) s.Execute("UPDATE messaging_dedup SET created=$1 WHERE message_id=(SELECT message_id FROM messaging_messages WHERE seq=$2)", Now, seq);
                    if (terminal) s.Execute("INSERT OR REPLACE INTO messaging_failures(consumer,seq,error,failed_at) VALUES($1,$2,$3,$4)", consumer, seq, (error ?? "Retry limit reached")[..Math.Min(2048, (error ?? "Retry limit reached").Length)], Now);
                    if (ack && commitOutbox) foreach (var message in Outbox) Insert(s, message);
                    return true;
                }).GetAwaiter().GetResult();
                Completed = true;
                owner.SignalConsumer(consumer);
                if (ack && commitOutbox) foreach (var message in Outbox) owner.SignalSubject(message.Subject);
            }
            return Task.CompletedTask;
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
