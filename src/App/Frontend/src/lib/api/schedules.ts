import { getJson, sendEmpty, sendJson } from './http';

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
}

export type ScheduleUpdateRequest = Omit<ScheduleCreateRequest, 'key'>;

/** Task types registered in the Scheduler's TaskTypeRegistry; unregistered types are ignored by the scheduler. */
export const scheduleTaskTypes = [
  'orphan_metadata_cleanup',
  'channel_update_check',
  'channel_asset_refresh',
  'channel_media_list',
  'stale_database_cleanup',
  'watched_item_auto_delete',
  'database_maintenance',
  'search_reindex',
  'filesystem_rescan',
  'processed_message_cleanup',
  'backup'
] as const;

const BASE = '/api/global/schedules';

export async function listSchedules(fetchImpl: typeof fetch = fetch): Promise<ScheduledTask[]> {
  return getJson<ScheduledTask[]>(BASE, fetchImpl);
}

export async function getSchedule(key: string, fetchImpl: typeof fetch = fetch): Promise<ScheduledTask> {
  return getJson<ScheduledTask>(`${BASE}/${encodeURIComponent(key)}`, fetchImpl);
}

export async function createSchedule(
  request: ScheduleCreateRequest,
  fetchImpl: typeof fetch = fetch
): Promise<ScheduledTask> {
  return sendJson<ScheduledTask>(BASE, 'POST', request, fetchImpl);
}

export async function updateSchedule(
  key: string,
  request: ScheduleUpdateRequest,
  fetchImpl: typeof fetch = fetch
): Promise<ScheduledTask> {
  return sendJson<ScheduledTask>(`${BASE}/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function deleteSchedule(key: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  return sendEmpty(`${BASE}/${encodeURIComponent(key)}`, 'DELETE', fetchImpl);
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
