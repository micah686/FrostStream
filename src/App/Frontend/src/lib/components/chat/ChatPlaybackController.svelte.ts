import { fetchChatWindow, type ChatMessage } from '$lib/api/liveChat';

/** Messages fetched ahead of the playhead before another range request is issued. */
const PREFETCH_LEAD_MS = 30_000;
/** Length of each forward prefetch window. */
const PREFETCH_SPAN_MS = 120_000;
/** A jump larger than this is treated as a seek rather than normal playback drift. */
const SEEK_THRESHOLD_MS = 2_000;
/** Messages kept behind the playhead; older ones are dropped so long sessions stay bounded. */
const MAX_VISIBLE = 400;

/**
 * Keeps a chat replay in sync with video playback. Owns the buffer and the fetch policy; the
 * virtualizer that renders `visible` knows nothing about playback, and this class knows nothing
 * about the DOM.
 *
 * Playback drives it through `tick(positionMs)`, which reveals buffered messages up to the
 * playhead and prefetches forward. Large jumps are detected as seeks and refill the buffer from
 * scratch around the new position, cancelling any in-flight request.
 */
export class ChatPlaybackController {
  #mediaGuid: string;
  #buffer: ChatMessage[] = [];
  /** Index into #buffer of the first message not yet revealed. */
  #cursor = 0;
  /** Highest offset covered by fetched data; -1 until the first load. */
  #bufferedToMs = -1;
  #lastPositionMs = 0;
  #inFlight: AbortController | null = null;
  #pendingSeekMs: number | null = null;
  #disposed = false;

  visible = $state<ChatMessage[]>([]);
  loading = $state(false);
  error = $state<string | null>(null);

  constructor(mediaGuid: string) {
    this.#mediaGuid = mediaGuid;
  }

  /** Feeds the current playback position; safe to call at the player's ~4 Hz progress rate. */
  tick(positionMs: number): void {
    if (this.#disposed) {
      return;
    }

    const delta = positionMs - this.#lastPositionMs;
    this.#lastPositionMs = positionMs;

    // Backwards or a big forward jump means the viewer seeked: the buffer no longer describes
    // the region being played.
    if (this.#bufferedToMs >= 0 && (delta < -SEEK_THRESHOLD_MS || delta > SEEK_THRESHOLD_MS)) {
      void this.seek(positionMs);
      return;
    }

    if (this.#bufferedToMs < 0) {
      void this.seek(positionMs);
      return;
    }

    this.#reveal(positionMs);

    if (this.#bufferedToMs - positionMs < PREFETCH_LEAD_MS) {
      void this.#prefetch();
    }
  }

  /** Refills the buffer around a position, dropping whatever is in flight. */
  async seek(positionMs: number): Promise<void> {
    if (this.#disposed) {
      return;
    }

    this.#lastPositionMs = positionMs;
    const signal = this.#beginRequest();
    this.#pendingSeekMs = positionMs;

    try {
      const messages = await fetchChatWindow(
        this.#mediaGuid,
        { around: positionMs, before: 50, after: 300 },
        signal
      );
      if (signal.aborted || this.#disposed) {
        return;
      }

      this.#buffer = messages;
      // Everything at or before the playhead is already "past" — show it as scrollback.
      this.#cursor = 0;
      this.#bufferedToMs = messages.length > 0
        ? messages[messages.length - 1].videoOffsetMs
        : positionMs;
      this.visible = [];
      this.#reveal(positionMs);
      this.error = null;
    } catch (err) {
      if (!signal.aborted && !this.#disposed) {
        this.error = describeError(err);
      }
    } finally {
      this.#endRequest(signal);
      this.#pendingSeekMs = null;
    }
  }

  dispose(): void {
    this.#disposed = true;
    this.#inFlight?.abort();
    this.#inFlight = null;
  }

  /** Moves buffered messages at or before the playhead into `visible`. */
  #reveal(positionMs: number): void {
    let revealed = 0;
    while (this.#cursor < this.#buffer.length && this.#buffer[this.#cursor].videoOffsetMs <= positionMs) {
      revealed++;
      this.#cursor++;
    }

    if (revealed === 0) {
      return;
    }

    const next = this.visible.concat(this.#buffer.slice(this.#cursor - revealed, this.#cursor));
    this.visible = next.length > MAX_VISIBLE ? next.slice(next.length - MAX_VISIBLE) : next;
  }

  /** Extends the buffer forward from the last known offset. */
  async #prefetch(): Promise<void> {
    if (this.#inFlight || this.#pendingSeekMs !== null || this.#disposed) {
      return;
    }

    const from = this.#bufferedToMs + 1;
    const to = from + PREFETCH_SPAN_MS;
    const signal = this.#beginRequest();

    try {
      const messages = await fetchChatWindow(this.#mediaGuid, { from, to, limit: 500 }, signal);
      if (signal.aborted || this.#disposed) {
        return;
      }

      if (messages.length > 0) {
        // Drop already-revealed messages so the buffer (and its cursor) stay small.
        this.#buffer = this.#buffer.slice(this.#cursor).concat(messages);
        this.#cursor = 0;
        this.#bufferedToMs = messages[messages.length - 1].videoOffsetMs;
      } else {
        // No messages in this span — advance the watermark so we don't re-request it.
        this.#bufferedToMs = to;
      }
      this.#reveal(this.#lastPositionMs);
      this.error = null;
    } catch (err) {
      if (!signal.aborted && !this.#disposed) {
        this.error = describeError(err);
      }
    } finally {
      this.#endRequest(signal);
    }
  }

  #beginRequest(): AbortSignal {
    this.#inFlight?.abort();
    const controller = new AbortController();
    this.#inFlight = controller;
    this.loading = true;
    return controller.signal;
  }

  #endRequest(signal: AbortSignal): void {
    if (this.#inFlight?.signal === signal) {
      this.#inFlight = null;
      this.loading = false;
    }
  }
}

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : 'Live chat could not be loaded.';
}
