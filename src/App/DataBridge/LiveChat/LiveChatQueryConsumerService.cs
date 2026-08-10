using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;
using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.LiveChat;
using Shared.Messaging;

namespace DataBridge.LiveChat;

/// <summary>
/// Serves playback-oriented chat window queries over NATS request/reply. Registered only when
/// live chat is enabled — WebAPI short-circuits to 404 when disabled, so nothing requests the
/// subject either. Queries read a narrow (media_guid, video_offset_ms) range and resolve the
/// deduplicated fragment payloads with an ANY LEFT JOIN; <c>LIMIT 1 BY message_id</c> guards
/// against pre-merge ReplacingMergeTree duplicates.
/// </summary>
public sealed class LiveChatQueryConsumerService(
    IMessageBus messageBus,
    ClickHouseAccess clickHouse,
    IOptions<LiveChatOptions> options,
    ILogger<LiveChatQueryConsumerService> logger) : SubscriptionBackgroundService
{
    private const string SelectClause = """
        SELECT
            m.message_id,
            m.video_offset_ms,
            m.message_type,
            toUnixTimestamp64Milli(m.published_at) AS published_ms,
            m.author_external_id,
            m.author_name,
            m.author_badges,
            t.fragments AS fragments,
            m.amount_text,
            m.currency,
            m.header_color,
            m.body_color
        FROM live_chat_messages AS m
        ANY LEFT JOIN live_chat_message_texts AS t ON m.fragments_hash = t.fragments_hash
        """;

    private readonly LiveChatOptions _options = options.Value;

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<LiveChatWindowRequestMessage>(
            messageBus,
            LiveChatSubjects.WindowGet,
            HandleWindowAsync,
            queueGroup: LiveChatSubjects.QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to live chat window queries.");
    }

    private async Task HandleWindowAsync(IMessageContext<LiveChatWindowRequestMessage> context)
    {
        var request = context.Message;
        try
        {
            await using var connection = clickHouse.CreateConnection();
            await connection.OpenAsync(CancellationToken.None);

            List<LiveChatMessageDto> messages;
            if (request.AroundMs is { } around)
            {
                var before = Clamp(request.Before);
                var after = Clamp(request.After);

                messages = await QueryAsync(connection, request.MediaGuid,
                    "AND m.video_offset_ms < {edge:Int64} ORDER BY m.video_offset_ms DESC, m.message_id",
                    around, before);
                messages.Reverse();

                messages.AddRange(await QueryAsync(connection, request.MediaGuid,
                    "AND m.video_offset_ms >= {edge:Int64} ORDER BY m.video_offset_ms ASC, m.message_id",
                    around, after));
            }
            else
            {
                var from = request.FromMs ?? 0;
                var to = request.ToMs ?? long.MaxValue;
                messages = await QueryAsync(connection, request.MediaGuid,
                    "AND m.video_offset_ms >= {edge:Int64} AND m.video_offset_ms <= {to:Int64} " +
                    "ORDER BY m.video_offset_ms ASC, m.message_id",
                    from, Clamp(request.Limit),
                    command => command.AddParameter("to", to));
            }

            await context.RespondAsync(new LiveChatWindowResponseMessage
            {
                Success = true,
                Messages = messages
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling live chat window query for {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new LiveChatWindowResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Live chat query failed."
            });
        }
    }

    private static async Task<List<LiveChatMessageDto>> QueryAsync(
        ClickHouseConnection connection,
        Guid mediaGuid,
        string filterAndOrder,
        long edge,
        int limit,
        Action<ClickHouseCommand>? configure = null)
    {
        if (limit <= 0)
            return [];

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"{SelectClause} WHERE m.media_guid = {{mediaGuid:UUID}} {filterAndOrder} " +
            "LIMIT 1 BY m.message_id LIMIT {limit:Int32}";
        command.AddParameter("mediaGuid", mediaGuid);
        command.AddParameter("edge", edge);
        command.AddParameter("limit", limit);
        configure?.Invoke(command);

        var messages = new List<LiveChatMessageDto>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var publishedMs = reader.GetInt64(3);
            var fragments = reader.IsDBNull(7) ? "" : reader.GetString(7);
            messages.Add(new LiveChatMessageDto
            {
                MessageId = reader.GetString(0),
                VideoOffsetMs = reader.GetInt64(1),
                Type = reader.GetString(2),
                PublishedAtUnixMs = publishedMs > 0 ? publishedMs : null,
                AuthorExternalId = reader.GetString(4),
                AuthorName = reader.GetString(5),
                Badges = ReadStringArray(reader.GetValue(6)),
                FragmentsJson = fragments.Length > 0 ? fragments : "[]",
                AmountText = reader.GetString(8) is { Length: > 0 } amount ? amount : null,
                Currency = reader.GetString(9) is { Length: > 0 } currency ? currency : null,
                HeaderColor = Convert.ToUInt32(reader.GetValue(10)) is var header and > 0u ? header : null,
                BodyColor = Convert.ToUInt32(reader.GetValue(11)) is var body and > 0u ? body : null
            });
        }

        return messages;
    }

    /// <summary>ClickHouse arrays surface as string[] or object[] depending on the column type.</summary>
    private static IReadOnlyList<string> ReadStringArray(object? value) => value switch
    {
        string[] strings => strings,
        System.Collections.IEnumerable items and not string =>
            items.Cast<object?>().Select(static item => item?.ToString() ?? "").ToArray(),
        _ => []
    };

    private int Clamp(int requested)
        => Math.Clamp(requested, 0, Math.Max(1, _options.MaxWindowRows));
}
