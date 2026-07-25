import { writable, type Readable } from 'svelte/store';
import {
  fetchRenditionQueue,
  renditionQueueStreamUrl,
  type RenditionQueueItem,
  type RenditionQueueListParams
} from '$lib/api/encodingQueue';
import type { RenditionProgressFrame } from '$lib/api/channelAudio';
import { readEventStream, type EventStreamHandlers } from '$lib/sse/eventStream';

export interface EncodingQueueRow {
  item: RenditionQueueItem;
  progress?: RenditionProgressFrame;
}

export interface EncodingQueueState {
  rows: EncodingQueueRow[];
  totalCount: number;
  nextCursor: string | null;
  connected: boolean;
  loading: boolean;
  error: string | null;
}

export interface EncodingQueueStore extends Readable<EncodingQueueState> {
  connect(): void;
  disconnect(): void;
  setParams(params: RenditionQueueListParams): Promise<void>;
  refresh(): Promise<void>;
}

export interface EncodingQueueStoreDeps {
  snapshot: (
    params: RenditionQueueListParams
  ) => Promise<{ rows: RenditionQueueItem[]; totalCount: number; nextCursor: string | null }>;
  openStream: (handlers: EventStreamHandlers, signal: AbortSignal) => Promise<void>;
  backoffMs?: (attempt: number) => number;
}

const defaultDeps: EncodingQueueStoreDeps = {
  snapshot: async (params) => {
    const response = await fetchRenditionQueue(params);
    return { rows: response.items, totalCount: response.totalCount, nextCursor: response.nextCursor };
  },
  openStream: (handlers, signal) => readEventStream(renditionQueueStreamUrl(), handlers, signal),
  backoffMs: (attempt) => Math.min(1000 * 2 ** attempt, 15000)
};

export function createEncodingQueueStore(deps: EncodingQueueStoreDeps = defaultDeps): EncodingQueueStore {
  const backoff = deps.backoffMs ?? defaultDeps.backoffMs!;
  const rows = new Map<string, EncodingQueueRow>();
  const store = writable<EncodingQueueState>({
    rows: [],
    totalCount: 0,
    nextCursor: null,
    connected: false,
    loading: true,
    error: null
  });
  let controller: AbortController | null = null;
  let lastTotalCount = 0;
  let lastNextCursor: string | null = null;
  let currentParams: RenditionQueueListParams = { limit: 50 };

  const publish = (patch: Partial<Omit<EncodingQueueState, 'rows' | 'totalCount'>> = {}) =>
    store.update((state) => ({
      ...state,
      ...patch,
      rows: [...rows.values()],
      totalCount: lastTotalCount,
      nextCursor: lastNextCursor
    }));

  async function refresh(): Promise<void> {
    publish({ loading: true, error: null });
    try {
      const snapshot = await deps.snapshot(currentParams);
      seed(snapshot.rows);
      lastTotalCount = snapshot.totalCount;
      lastNextCursor = snapshot.nextCursor;
      publish({ loading: false, error: null });
    } catch (err) {
      publish({ loading: false, error: err instanceof Error ? err.message : String(err) });
      throw err;
    }
  }

  function seed(items: RenditionQueueItem[]): void {
    const previousProgress = new Map([...rows.values()].map((row) => [row.item.renditionId, row.progress]));
    rows.clear();
    for (const item of items) {
      rows.set(item.renditionId, { item, progress: previousProgress.get(item.renditionId) });
    }
  }

  function applyEvent(event: { event: string; data: string }): void {
    if (event.event !== 'progress') return;
    const frame = JSON.parse(event.data) as RenditionProgressFrame;
    const row = rows.get(frame.renditionId);
    if (!row) {
      // A rendition queued elsewhere after our last snapshot isn't on the current page yet.
      if (!currentParams.cursor) void refresh();
      return;
    }

    if (frame.phase === 'Ready' || frame.phase === 'Failed') {
      // The compact SSE frame omits status/size/error — refresh for the authoritative row.
      void refresh();
      return;
    }

    rows.set(frame.renditionId, { ...row, progress: frame });
    publish();
  }

  async function loop(signal: AbortSignal): Promise<void> {
    let attempt = 0;
    while (!signal.aborted) {
      try {
        await refresh();
        publish({ connected: true, error: null });
        attempt = 0;
        await deps.openStream({ onEvent: applyEvent }, signal);
      } catch (err) {
        if (signal.aborted) {
          break;
        }
        publish({ connected: false, loading: false, error: err instanceof Error ? err.message : String(err) });
      }

      if (signal.aborted) {
        break;
      }
      publish({ connected: false });
      await delay(backoff(attempt++), signal);
    }
  }

  return {
    subscribe: store.subscribe,
    connect() {
      if (controller) {
        return;
      }
      controller = new AbortController();
      void loop(controller.signal);
    },
    disconnect() {
      controller?.abort();
      controller = null;
      publish({ connected: false });
    },
    async setParams(params: RenditionQueueListParams) {
      currentParams = { ...params };
      await refresh();
    },
    refresh
  };
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  if (ms <= 0 || signal.aborted) {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    const timer = setTimeout(finish, ms);
    signal.addEventListener('abort', finish, { once: true });

    function finish() {
      clearTimeout(timer);
      signal.removeEventListener('abort', finish);
      resolve();
    }
  });
}
