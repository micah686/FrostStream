import { fetchChatWindow, type ChatMessage } from '$lib/api/liveChat';
import { ApiRequestError } from '$lib/api/http';

/** Buffer lead below which a top-up is issued. */
const PREFETCH_LEAD_MS = 30_000;
/** Buffer lead the prefetcher tops up to once it starts. Hysteresis against PREFETCH_LEAD_MS. */
const TARGET_LEAD_MS = 60_000;
/** Length of each forward prefetch window. */
const PREFETCH_SPAN_MS = 120_000;
/**
 * Rows per window request. Matches LiveChatOptions.MaxWindowRows, the server-side clamp — asking
 * for less just means more round trips, and on a busy chat the row cap, not the time span, is what
 * limits how much video each request covers (1000 rows is only ~10s of a 100 msg/s stream).
 */
const WINDOW_ROW_LIMIT = 1_000;
/** Back-to-back top-ups allowed per prefetch cycle, so a busy chat reaches TARGET_LEAD_MS without
 * waiting a whole tick between requests. */
const MAX_PREFETCH_CHAIN = 4;
/** A jump larger than this is treated as a seek rather than normal playback drift. */
const SEEK_THRESHOLD_MS = 2_000;
/** Messages kept behind the playhead; older ones are dropped so long sessions stay bounded.
 * Only the on-screen rows are in the DOM (the list is virtualized), so this is really a scrollback
 * depth: at 100 msg/s a smaller cap would discard messages seconds after they appear. */
const MAX_VISIBLE = 1_000;

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
  /** Guards the whole top-up chain, which spans several awaits with no request in flight between. */
  #prefetching = false;
  #disposed = false;
  /** A 404 means this deployment cannot serve this replay; never retry it on every video frame. */
  #unavailable = false;

  visible = $state<ChatMessage[]>([]);
  loading = $state(false);
  error = $state<string | null>(null);

  constructor(mediaGuid: string) {
    this.#mediaGuid = mediaGuid;
  }

  /** Feeds the current playback position; safe to call at the player's ~4 Hz progress rate. */
  tick(positionMs: number): void {
    if (this.#disposed || this.#unavailable) {
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
    if (this.#disposed || this.#unavailable) {
      return;
    }

    this.#lastPositionMs = positionMs;
    const signal = this.#beginRequest();
    this.#pendingSeekMs = positionMs;

    try {
      const messages = await fetchChatWindow(
        this.#mediaGuid,
        { around: positionMs, before: 100, after: 400 },
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
        this.#unavailable = err instanceof ApiRequestError && err.status === 404;
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

  /**
   * Extends the buffer forward until it leads the playhead by {@link TARGET_LEAD_MS}.
   *
   * A single request can only return {@link WINDOW_ROW_LIMIT} rows, which on a busy chat is a few
   * seconds of video — one request per tick would leave the buffer permanently grazing the
   * playhead and the replay stalling between windows. Chaining the top-ups fills the lead in one
   * cycle instead.
   */
  async #prefetch(): Promise<void> {
    if (this.#prefetching || this.#inFlight || this.#pendingSeekMs !== null || this.#disposed || this.#unavailable) {
      return;
    }

    this.#prefetching = true;
    try {
      for (let i = 0; i < MAX_PREFETCH_CHAIN; i++) {
        if (this.#disposed || this.#pendingSeekMs !== null) {
          break;
        }
        if (this.#bufferedToMs - this.#lastPositionMs >= TARGET_LEAD_MS) {
          break;
        }
        if (!(await this.#fetchForward())) {
          break;
        }
      }
    } finally {
      this.#prefetching = false;
      this.loading = this.#inFlight !== null;
    }
  }

  /** One forward window request. Returns false when the top-up chain should stop. */
  async #fetchForward(): Promise<boolean> {
    const from = this.#bufferedToMs + 1;
    const to = from + PREFETCH_SPAN_MS;
    const signal = this.#beginRequest();

    try {
      const messages = await fetchChatWindow(
        this.#mediaGuid,
        { from, to, limit: WINDOW_ROW_LIMIT },
        signal
      );
      if (signal.aborted || this.#disposed) {
        return false;
      }

      if (messages.length > 0) {
        // Drop already-revealed messages so the buffer (and its cursor) stay small.
        this.#buffer = this.#buffer.slice(this.#cursor).concat(messages);
        this.#cursor = 0;
        this.#bufferedToMs = messages[messages.length - 1].videoOffsetMs;
      } else {
        // No messages in this span — advance the watermark so we don't re-request it. This also
        // ends the chain, since the lead now jumps a whole span ahead of the playhead.
        this.#bufferedToMs = to;
      }
      this.#reveal(this.#lastPositionMs);
      this.error = null;
      return true;
    } catch (err) {
      if (!signal.aborted && !this.#disposed) {
        this.#unavailable = err instanceof ApiRequestError && err.status === 404;
        this.error = describeError(err);
      }
      return false;
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
      // Mid-chain the next request follows immediately; holding the flag stops the spinner
      // strobing once per top-up.
      this.loading = this.#prefetching;
    }
  }
}

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : 'Live chat could not be loaded.';
}
