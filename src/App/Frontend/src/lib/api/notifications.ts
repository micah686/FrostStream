import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export interface NotificationPreferences {
  version: number;
  enabled: boolean;
  providers: NotificationProvider[];
  rules: NotificationRule[];
}

export interface NotificationProvider {
  providerKey: string;
  providerKind: string;
  enabled: boolean;
  displayName: string | null;
  defaultTo: string | null;
  notifyConfig: Record<string, unknown>;
}

export interface NotificationRule {
  eventKey: string;
  enabled: boolean;
  providerKeys: string[];
}

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
