-- Live chat replay storage. Chat data is a derived projection of the live_chat.json sidecars
-- in blob storage: every table here can be dropped and rebuilt via the backfill job.

CREATE TABLE IF NOT EXISTS live_chat_messages (
    media_guid         UUID,
    video_offset_ms    Int64,
    message_id         String,
    message_type       LowCardinality(String),
    published_at       DateTime64(3, 'UTC') DEFAULT toDateTime64(0, 3),
    author_external_id String,
    author_name        String,
    author_badges      Array(LowCardinality(String)),
    -- Fragment payloads are deduplicated into live_chat_message_texts: bursts of identical
    -- messages ("welcome", spammed emote combos) store the 8-byte hash only.
    fragments_hash     UInt64,
    amount_text        String DEFAULT '',
    currency           LowCardinality(String) DEFAULT '',
    header_color       UInt32 DEFAULT 0,
    body_color         UInt32 DEFAULT 0,
    ingested_at        DateTime DEFAULT now()
) ENGINE = ReplacingMergeTree(ingested_at)
  ORDER BY (media_guid, video_offset_ms, message_id);

CREATE TABLE IF NOT EXISTS live_chat_message_texts (
    fragments_hash UInt64,
    fragments      String CODEC(ZSTD(3))
) ENGINE = ReplacingMergeTree
  ORDER BY fragments_hash;

CREATE TABLE IF NOT EXISTS live_chat_emotes (
    emote_id     String,
    name         String,
    source_url   String,
    storage_key  String,
    storage_path String,
    updated_at   DateTime DEFAULT now()
) ENGINE = ReplacingMergeTree(updated_at)
  ORDER BY emote_id;
