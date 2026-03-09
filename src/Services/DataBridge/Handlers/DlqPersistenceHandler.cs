using System.Text;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;

namespace DataBridge.Handlers;

/// <summary>
/// Handler that listens to the DLQ stream and persists DLQ messages to the PostgreSQL database.
/// This ensures all failed messages are tracked in the database for analysis and remediation.
/// </summary>
public class DlqPersistenceHandler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DlqPersistenceHandler> _logger;

    public DlqPersistenceHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<DlqPersistenceHandler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DLQ Persistence Handler starting...");

        using var scope = _scopeFactory.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<IJetStreamConsumer>();
        var dbContext = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

        // Ensure database is available
        await dbContext.Database.CanConnectAsync(stoppingToken);

        // Create a consumer on the DLQ stream
        // Note: Stream name for DLQ should match the topology configuration
        await consumer.ConsumeAsync<DlqMessage>(
            StreamName.From("FS_DLQ"),
            SubjectName.From(Subjects.DeadLetter),
            async (context) =>
            {
                await HandleDlqMessageAsync(context, stoppingToken);
            },
            JetStreamConsumeOptions.Default,
            stoppingToken);
    }

    private async Task HandleDlqMessageAsync(IJsMessageContext<DlqMessage> context, CancellationToken cancellationToken)
    {
        var message = context.Message;
        var entryKey = $"{message.OriginalStream}.{message.OriginalConsumer}.{message.OriginalSequence}";

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

        try
        {
            // Check if already exists (idempotency)
            var exists = await dbContext.DlqEntries
                .AsNoTracking()
                .AnyAsync(e => e.EntryKey == entryKey, cancellationToken);

            if (exists)
            {
                _logger.LogDebug("DLQ entry {EntryKey} already exists, acknowledging message", entryKey);
                await context.AckAsync(cancellationToken);
                return;
            }

            // Extract correlation ID and job ID from headers if available
            string? correlationId = null;
            Guid? jobId = null;

            if (message.OriginalHeaders != null)
            {
                message.OriginalHeaders.TryGetValue("X-Correlation-Id", out correlationId);
                
                if (message.OriginalHeaders.TryGetValue("X-Job-Id", out var jobIdStr) && 
                    Guid.TryParse(jobIdStr, out var parsedJobId))
                {
                    jobId = parsedJobId;
                }
            }

            // Convert payload to string for storage (may be truncated)
            string? payloadString = null;
            long payloadSize = 0;

            if (message.Payload != null && message.Payload.Length > 0)
            {
                payloadSize = message.Payload.Length;
                
                // Store as base64 if binary, or UTF8 string if text
                if (message.PayloadEncoding?.StartsWith("objstore://") == true)
                {
                    // Payload is stored in object store, just store the reference
                    payloadString = $"[ObjectStore: {message.PayloadEncoding}]";
                }
                else if (message.PayloadEncoding?.StartsWith("truncated:") == true)
                {
                    payloadString = $"[Truncated: {message.PayloadEncoding}]";
                }
                else
                {
                    // Try to decode as UTF8 string, fallback to base64
                    try
                    {
                        payloadString = Encoding.UTF8.GetString(message.Payload);
                        // Limit stored payload size to 100KB
                        if (payloadString.Length > 100_000)
                        {
                            payloadString = payloadString[..100_000] + "\n[...truncated]";
                        }
                    }
                    catch
                    {
                        payloadString = Convert.ToBase64String(message.Payload);
                    }
                }
            }

            var entry = new DlqEntry
            {
                Id = Guid.NewGuid(),
                EntryKey = entryKey,
                OriginalStream = message.OriginalStream,
                OriginalConsumer = message.OriginalConsumer,
                OriginalSubject = message.OriginalSubject,
                OriginalSequence = message.OriginalSequence,
                DeliveryCount = message.DeliveryCount,
                FailedAt = message.FailedAt,
                StoredAt = DateTimeOffset.UtcNow,
                ErrorReason = message.ErrorReason,
                StackTrace = null, // Not available in DlqMessage
                Payload = payloadString,
                PayloadContentType = message.PayloadEncoding,
                PayloadSize = payloadSize,
                MessageType = message.OriginalMessageType,
                CorrelationId = correlationId,
                JobId = jobId,
                Status = DlqEntryStatus.Pending
            };

            dbContext.DlqEntries.Add(entry);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Persisted DLQ entry {EntryId} for {Stream}/{Consumer} (Seq: {Sequence}, JobId: {JobId})",
                entry.Id, message.OriginalStream, message.OriginalConsumer, message.OriginalSequence, jobId);

            await context.AckAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another instance stored it concurrently
            _logger.LogDebug("DLQ entry {EntryKey} was stored by another instance, acknowledging", entryKey);
            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist DLQ entry {EntryKey}", entryKey);
            // NAK to retry
            await context.NackAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505";
    }
}
