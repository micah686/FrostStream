import { writable, type Readable } from 'svelte/store';
import {
  fetchQueue,
  queueStreamUrl,
  type DownloadQueueJob,
  type QueueListParams,
  type ProgressFrame,
  type StateFrame
} from '$lib/api/downloadQueue';
import { readEventStream, type EventStreamHandlers } from '$lib/sse/eventStream';

export interface QueueRow {
  job: DownloadQueueJob;
  progress?: ProgressFrame;
}

export interface DownloadQueueState {
  rows: QueueRow[];
  totalCount: number;
  nextCursor: string | null;
  connected: boolean;
  loading: boolean;
  error: string | null;
}

export interface DownloadQueueStore extends Readable<DownloadQueueState> {
  connect(): void;
  disconnect(): void;
  setParams(params: QueueListParams): Promise<void>;
  refresh(): Promise<void>;
}

export interface DownloadQueueStoreDeps {
  snapshot: (params: QueueListParams) => Promise<{ rows: DownloadQueueJob[]; totalCount: number; nextCursor: string | null }>;
  openStream: (handlers: EventStreamHandlers, signal: AbortSignal) => Promise<void>;
  backoffMs?: (attempt: number) => number;
}

const defaultDeps: DownloadQueueStoreDeps = {
  snapshot: async (params) => {
    const response = await fetchQueue(params);
    return { rows: response.items, totalCount: response.totalCount, nextCursor: response.nextCursor };
  },
  openStream: (handlers, signal) => readEventStream(queueStreamUrl(), handlers, signal),
  backoffMs: (attempt) => Math.min(1000 * 2 ** attempt, 15000)
};

export function createDownloadQueueStore(deps: DownloadQueueStoreDeps = defaultDeps): DownloadQueueStore {
  const backoff = deps.backoffMs ?? defaultDeps.backoffMs!;
  const rows = new Map<string, QueueRow>();
  const store = writable<DownloadQueueState>({
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
  let currentParams: QueueListParams = { limit: 50, sort: 'createdAt' };

  const publish = (patch: Partial<Omit<DownloadQueueState, 'rows' | 'totalCount'>> = {}) =>
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

  function seed(jobs: DownloadQueueJob[]): void {
    const previousProgress = new Map([...rows.values()].map((row) => [row.job.jobId, row.progress]));
    rows.clear();
    for (const job of jobs) {
      rows.set(job.jobId, { job, progress: previousProgress.get(job.jobId) });
    }
  }

  function applyEvent(event: { event: string; data: string }): void {
    if (event.event === 'progress') {
      const frame = JSON.parse(event.data) as ProgressFrame;
      const row = rows.get(frame.jobId);
      if (row) {
        rows.set(frame.jobId, { ...row, progress: frame });
      }
    } else if (event.event === 'state') {
      const frame = JSON.parse(event.data) as StateFrame;
      const row = rows.get(frame.jobId);
      if (row) {
        if (!stateMatchesFilter(frame.state, currentParams.state, currentParams.stateGroup)) {
          void refresh();
          return;
        }
        rows.set(frame.jobId, {
          ...row,
          job: { ...row.job, state: frame.state, updatedAt: frame.occurredAt }
        });
      } else {
        if (!currentParams.cursor) {
          void refresh();
        }
      }
    }
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
    async setParams(params: QueueListParams) {
      currentParams = { ...params };
      await refresh();
    },
    refresh
  };
}

function stateMatchesFilter(state: string, exactState?: string, stateGroup?: string): boolean {
  if (exactState) {
    return normalizeState(state) === normalizeState(exactState);
  }

  const normalized = normalizeState(state);
  switch (stateGroup) {
    case 'active':
      return [
        'metadatapending',
        'metadataresolved',
        'downloadpending',
        'uploadpending',
        'commitpending',
        'compensating',
        'cancelling'
      ].includes(normalized);
    case 'queued':
      return ['queued', 'downloadqueued'].includes(normalized);
    case 'failed':
      return ['failedtransient', 'failedpermanent', 'deadlettered', 'providerhalted'].includes(normalized);
    case 'done':
      return ['completed', 'alreadydownloaded'].includes(normalized);
    case 'cancelled':
      return ['cancelled', 'ignored'].includes(normalized);
    default:
      return true;
  }
}

function normalizeState(state: string): string {
  return state.toLowerCase();
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
