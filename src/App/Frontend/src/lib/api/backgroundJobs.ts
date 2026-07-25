import { getJson } from '$lib/api/http';

export type BackgroundRunStatus = 'running' | 'completed' | 'failed';

export interface BackgroundRunLogLine {
  message: string;
  at: string;
}

export interface BackgroundRun {
  runId: string;
  taskType: string;
  scheduleKey: string;
  origin: string;
  detail: string | null;
  status: BackgroundRunStatus;
  message: string | null;
  current: number | null;
  total: number | null;
  percent: number | null;
  startedAt: string;
  completedAt: string | null;
  errorMessage: string | null;
  summary: string | null;
  log: BackgroundRunLogLine[];
}

export interface BackgroundRunListResponse {
  items: BackgroundRun[];
  runningCount: number;
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
