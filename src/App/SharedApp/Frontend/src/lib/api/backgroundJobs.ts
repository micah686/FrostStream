import { getJson } from '$lib/api/http';

/** `queued` means the schedule fired and no service has picked the work up yet. */
export type BackgroundRunStatus = 'queued' | 'running' | 'completed' | 'failed';

export interface BackgroundRunLogLine {
  message: string;
  at: string;
}

export type BackgroundRunTrigger = 'Scheduled' | 'Manual';

export interface BackgroundRun {
  runId: string;
  taskType: string;
  /** Null for runs a user started directly — an admin-triggered backup has no schedule behind it. */
  scheduleKey: string | null;
  trigger: BackgroundRunTrigger;
  origin: string;
  detail: string | null;
  status: BackgroundRunStatus;
  message: string | null;
  current: number | null;
  total: number | null;
  percent: number | null;
  /** While queued this is the moment the schedule fired; it becomes the real start on pickup. */
  startedAt: string;
  /** Set once the scheduler announced the firing, so the wait before pickup stays visible. */
  queuedAt: string | null;
  completedAt: string | null;
  errorMessage: string | null;
  summary: string | null;
  log: BackgroundRunLogLine[];
}

export interface BackgroundRunListResponse {
  items: BackgroundRun[];
  runningCount: number;
  queuedCount: number;
}

/** Progress frame pushed on the SSE stream; merged into the matching run row. */
export interface BackgroundRunProgressFrame {
  runId: string;
  message: string;
  current: number | null;
  total: number | null;
  percent: number | null;
  occurredAt: string;
}

const BASE = '/api/jobs/background';

export const backgroundRunStreamUrl = (): string => `${BASE}/stream`;

export async function fetchBackgroundRuns(
  fetchImpl: typeof fetch = fetch
): Promise<BackgroundRunListResponse> {
  return getJson<BackgroundRunListResponse>(BASE, fetchImpl);
}

/** Turns `search_reindex` into `Search reindex` for display. */
export function humanizeTaskType(taskType: string): string {
  const spaced = taskType.replace(/[_-]+/g, ' ').trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
