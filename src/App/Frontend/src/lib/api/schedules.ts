import { getJson, sendJson } from './http';

export type ScheduleCatchupPolicy = 'Coalesce' | 'Skip';

export interface ScheduledTask {
  id: number;
  key: string;
  taskType: string;
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  catchupPolicy: ScheduleCatchupPolicy;
  retentionDays: number;
  includeFailed: boolean;
  lastAttemptAt: string | null;
  lastSuccessAt: string | null;
  nextDueAt: string | null;
  createdAt: string;
  lastUpdated: string | null;
}

export interface ScheduleCreateRequest {
  key: string;
  taskType: string;
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  catchupPolicy: ScheduleCatchupPolicy;
  retentionDays: number;
  includeFailed: boolean;
}

export type ScheduleUpdateRequest = Omit<ScheduleCreateRequest, 'key'>;

/** Task types registered in the Scheduler's TaskTypeRegistry; unregistered types are ignored by the scheduler. */
export const scheduleTaskTypes = [
  'channel_scan_refresh',
  'channel-asset-refresh',
  'channel_scan_full',
  'db-stale-media-cleanup',
  'db-maintenance',
  'database_maintenance_reindex',
  'search-reindex',
  'download-history-cleanup',
  'import_session_cleanup',
  'backup-full',
  'backup-diff'
] as const;

const BASE = '/api/global/schedules';

export async function listSchedules(fetchImpl: typeof fetch = fetch): Promise<ScheduledTask[]> {
  return getJson<ScheduledTask[]>(BASE, fetchImpl);
}

export async function updateSchedule(
  key: string,
  request: ScheduleUpdateRequest,
  fetchImpl: typeof fetch = fetch
): Promise<ScheduledTask> {
  return sendJson<ScheduledTask>(`${BASE}/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export function scheduleTimingSummary(schedule: ScheduledTask): string {
  if (schedule.cron) {
    return `cron: ${schedule.cron}`;
  }
  if (schedule.intervalSeconds != null) {
    return `every ${formatInterval(schedule.intervalSeconds)}`;
  }
  return 'no timing configured';
}

function formatInterval(totalSeconds: number): string {
  const units: [number, string][] = [
    [86400, 'd'],
    [3600, 'h'],
    [60, 'm'],
    [1, 's']
  ];
  const parts: string[] = [];
  let remaining = totalSeconds;
  for (const [size, suffix] of units) {
    if (remaining >= size) {
      parts.push(`${Math.floor(remaining / size)}${suffix}`);
      remaining %= size;
    }
  }
  return parts.join(' ') || '0s';
}
