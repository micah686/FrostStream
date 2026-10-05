import { writable, type Readable } from 'svelte/store';
import {
  backgroundRunStreamUrl,
  fetchBackgroundRuns,
  type BackgroundRun,
  type BackgroundRunProgressFrame
} from '$lib/api/backgroundJobs';
import { readEventStream, type EventStreamHandlers } from '$lib/sse/eventStream';

const MAX_LOG_LINES = 200;

export interface BackgroundJobsState {
  runs: BackgroundRun[];
  runningCount: number;
  /** Schedules that have fired but whose work no service has claimed yet. */
  queuedCount: number;
  connected: boolean;
  loading: boolean;
  error: string | null;
}

export interface BackgroundJobsStore extends Readable<BackgroundJobsState> {
  connect(): void;
  disconnect(): void;
  refresh(): Promise<void>;
}

export interface BackgroundJobsStoreDeps {
  snapshot: () => Promise<{ runs: BackgroundRun[] }>;
  openStream: (handlers: EventStreamHandlers, signal: AbortSignal) => Promise<void>;
  backoffMs?: (attempt: number) => number;
}

const defaultDeps: BackgroundJobsStoreDeps = {
  // Counts are derived from the rows themselves so the snapshot and the live stream can never
  // disagree about how many tasks are queued or running.
  snapshot: async () => ({ runs: (await fetchBackgroundRuns()).items }),
  openStream: (handlers, signal) => readEventStream(backgroundRunStreamUrl(), handlers, signal),
  backoffMs: (attempt) => Math.min(1000 * 2 ** attempt, 15000)
};

export function createBackgroundJobsStore(deps: BackgroundJobsStoreDeps = defaultDeps): BackgroundJobsStore {
  const backoff = deps.backoffMs ?? defaultDeps.backoffMs!;
  const runs = new Map<string, BackgroundRun>();
  const store = writable<BackgroundJobsState>({
    runs: [],
    runningCount: 0,
    queuedCount: 0,
    connected: false,
    loading: true,
    error: null
  });
  let controller: AbortController | null = null;

  const publish = (patch: Partial<Omit<BackgroundJobsState, 'runs' | 'runningCount' | 'queuedCount'>> = {}) =>
    store.update((state) => {
      const ordered = sortRuns([...runs.values()]);
      return {
        ...state,
        ...patch,
        runs: ordered,
        runningCount: ordered.filter((x) => x.status === 'running').length,
        queuedCount: ordered.filter((x) => x.status === 'queued').length
      };
    });

  async function refresh(): Promise<void> {
    publish({ loading: true, error: null });
    try {
      const snapshot = await deps.snapshot();
      runs.clear();
      for (const run of snapshot.runs) {
        runs.set(run.runId, run);
      }
      publish({ loading: false, error: null });
    } catch (err) {
      publish({ loading: false, error: err instanceof Error ? err.message : String(err) });
      throw err;
    }
  }

  function applyEvent(event: { event: string; data: string }): void {
    // 'queued' opens the row when a schedule fires; 'started' arrives with the same runId once a
    // service claims the work, so it replaces the queued row in place rather than adding one.
    if (event.event === 'queued' || event.event === 'started' || event.event === 'completed') {
      const run = JSON.parse(event.data) as BackgroundRun;
      runs.set(run.runId, run);
    } else if (event.event === 'progress') {
      const frame = JSON.parse(event.data) as BackgroundRunProgressFrame;
      const existing = runs.get(frame.runId);
      // A frame for a run we never saw start belongs to a run the server is not tracking for us;
      // ignore it rather than rendering a row with no identity.
      if (!existing) {
        return;
      }
      runs.set(frame.runId, {
        ...existing,
        message: frame.message,
        current: frame.current ?? existing.current,
        total: frame.total ?? existing.total,
        percent: frame.percent ?? existing.percent,
        log: [...existing.log, { message: frame.message, at: frame.occurredAt }].slice(-MAX_LOG_LINES)
      });
    } else {
      return;
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
    refresh
  };
}

/** Running first, then queued (longest-waiting at the top of each), then finished newest-first. */
function sortRuns(items: BackgroundRun[]): BackgroundRun[] {
  const rank = (run: BackgroundRun): number =>
    run.status === 'running' ? 0 : run.status === 'queued' ? 1 : 2;

  return items.sort((a, b) => {
    if (rank(a) !== rank(b)) {
      return rank(a) - rank(b);
    }
    if (rank(a) < 2) {
      return Date.parse(a.startedAt) - Date.parse(b.startedAt);
    }
    return Date.parse(b.completedAt ?? b.startedAt) - Date.parse(a.completedAt ?? a.startedAt);
  });
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
