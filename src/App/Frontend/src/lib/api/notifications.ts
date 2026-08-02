import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export interface NotificationPreferences {
  version: number;
  enabled: boolean;
  providers: NotificationProvider[];
}

export interface NotificationProvider {
  providerKey: string;
  providerKind: string;
  enabled: boolean;
  displayName: string | null;
  defaultTo: string | null;
  eventKeys?: NotificationEventKey[];
  notifyConfig: Record<string, unknown>;
}

export type NotificationEventKey =
  | 'download.completed'
  | 'download.failed-permanent'
  | 'download.provider-halted'
  | 'schedule.failed'
  | 'download.dead-lettered'
  | 'worker.unavailable'
  | 'storage.failed-permanent'
  | 'backup.failed'
  | 'index.rebuild.failed'
  | 'system.integration.failed';

export interface NotificationSecretsUpsertRequest {
  secrets: Record<string, string>;
}

export interface NotificationTestRequest {
  providerKey: string;
  subject: string | null;
  body: string | null;
}

export const NOTIFICATION_PROVIDER_KEY_PATTERN = /^[a-z0-9-]{2,100}$/;
export const NOTIFICATION_SECRET_NAME_PATTERN = /^[A-Za-z0-9_.-]{1,100}$/;

export const NOTIFICATION_PROVIDER_KINDS = [
  'email',
  'sms',
  'push',
  'whatsapp',
  'slack',
  'discord',
  'teams',
  'telegram',
  'facebook',
  'line',
  'viber',
  'mattermost',
  'rocketchat'
] as const;

export const NOTIFICATION_EVENT_OPTIONS: {
  key: NotificationEventKey;
  label: string;
  group: 'User events' | 'Admin operational events';
  description: string;
}[] = [
  {
    key: 'download.completed',
    label: 'Download completed',
    group: 'User events',
    description: 'A requested download finished successfully.'
  },
  {
    key: 'download.failed-permanent',
    label: 'Download failed permanently',
    group: 'User events',
    description: 'A download failed and will not retry automatically.'
  },
  {
    key: 'download.provider-halted',
    label: 'Provider halted',
    group: 'User events',
    description: 'A provider halt blocked download work; matching users and subscribed admin providers can receive it.'
  },
  {
    key: 'schedule.failed',
    label: 'Any schedule failed',
    group: 'Admin operational events',
    description: 'Any scheduled task failed.'
  },
  {
    key: 'download.dead-lettered',
    label: 'Download dead-lettered',
    group: 'Admin operational events',
    description: 'A download flow or message is stuck or exhausted.'
  },
  {
    key: 'worker.unavailable',
    label: 'Worker unavailable',
    group: 'Admin operational events',
    description: 'No eligible worker is available or leases are expiring repeatedly.'
  },
  {
    key: 'storage.failed-permanent',
    label: 'Storage failed permanently',
    group: 'Admin operational events',
    description: 'Storage upload, delete, or access failed after retries.'
  },
  {
    key: 'backup.failed',
    label: 'Backup failed',
    group: 'Admin operational events',
    description: 'A backup job failed.'
  },
  {
    key: 'index.rebuild.failed',
    label: 'Index rebuild failed',
    group: 'Admin operational events',
    description: 'Search index rebuild failed.'
  },
  {
    key: 'system.integration.failed',
    label: 'System integration failed',
    group: 'Admin operational events',
    description: 'A required integration such as NATS, OpenBAO, OpenFGA, or Typesense is unavailable.'
  }
];

const BASE = '/api/user/notifications';

export async function getNotificationPreferences(
  fetchImpl: typeof fetch = fetch
): Promise<NotificationPreferences> {
  return getJson<NotificationPreferences>(`${BASE}/preferences`, fetchImpl);
}

export async function updateNotificationPreferences(
  request: NotificationPreferences,
  fetchImpl: typeof fetch = fetch
): Promise<NotificationPreferences> {
  return sendJson<NotificationPreferences>(`${BASE}/preferences`, 'PUT', request, fetchImpl);
}

export async function listNotificationProviders(fetchImpl: typeof fetch = fetch): Promise<NotificationProvider[]> {
  return getJson<NotificationProvider[]>(`${BASE}/providers`, fetchImpl);
}

export async function getNotificationProvider(
  providerKey: string,
  fetchImpl: typeof fetch = fetch
): Promise<NotificationProvider> {
  return getJson<NotificationProvider>(`${BASE}/providers/${encodeURIComponent(providerKey)}`, fetchImpl);
}

export async function upsertNotificationProvider(
  providerKey: string,
  request: NotificationProvider,
  fetchImpl: typeof fetch = fetch
): Promise<NotificationProvider> {
  return sendJson<NotificationProvider>(`${BASE}/providers/${encodeURIComponent(providerKey)}`, 'PUT', request, fetchImpl);
}

export async function updateNotificationProviderEnabled(
  provider: NotificationProvider,
  enabled: boolean,
  fetchImpl: typeof fetch = fetch
): Promise<NotificationProvider> {
  return upsertNotificationProvider(provider.providerKey, { ...provider, enabled }, fetchImpl);
}

export async function deleteNotificationProvider(
  providerKey: string,
  fetchImpl: typeof fetch = fetch
): Promise<NotificationPreferences> {
  return sendJson<NotificationPreferences>(`${BASE}/providers/${encodeURIComponent(providerKey)}`, 'DELETE', undefined, fetchImpl);
}

export async function upsertNotificationProviderSecrets(
  providerKey: string,
  request: NotificationSecretsUpsertRequest,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  return sendJson<void>(`${BASE}/providers/${encodeURIComponent(providerKey)}/secrets`, 'PUT', request, fetchImpl);
}

export async function deleteNotificationProviderSecret(
  providerKey: string,
  secretName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  return sendEmpty(
    `${BASE}/providers/${encodeURIComponent(providerKey)}/secrets/${encodeURIComponent(secretName)}`,
    'DELETE',
    fetchImpl
  );
}

export async function sendTestNotification(
  request: NotificationTestRequest,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  return sendJson<void>(
    `${BASE}/test`,
    'POST',
    {
      ownerSubject: 'current-user',
      providerKey: request.providerKey,
      subject: request.subject,
      body: request.body
    },
    fetchImpl
  );
}
