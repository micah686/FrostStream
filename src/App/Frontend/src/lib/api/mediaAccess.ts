import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export interface ProviderPolicy {
  provider: string;
  groups: string[];
}

export interface AgePolicy {
  threshold: number;
  groups: string[];
}

const BASE = '/api/global/media-access';

export async function getMediaGroups(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<string[]> {
  return getJson<string[]>(`${BASE}/media/${encodeURIComponent(mediaGuid)}/groups`, fetchImpl);
}

export async function addMediaGroup(
  mediaGuid: string,
  groupName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(
    `${BASE}/media/${encodeURIComponent(mediaGuid)}/groups/${encodeURIComponent(groupName)}`,
    'POST',
    fetchImpl
  );
}

export async function removeMediaGroup(
  mediaGuid: string,
  groupName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(
    `${BASE}/media/${encodeURIComponent(mediaGuid)}/groups/${encodeURIComponent(groupName)}`,
    'DELETE',
    fetchImpl
  );
}

export async function clearMediaGroups(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/media/${encodeURIComponent(mediaGuid)}/groups`, 'DELETE', fetchImpl);
}

export async function listProviderPolicies(fetchImpl: typeof fetch = fetch): Promise<ProviderPolicy[]> {
  return getJson<ProviderPolicy[]>(`${BASE}/providers`, fetchImpl);
}

export async function addProviderGroup(
  provider: string,
  groupName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(
    `${BASE}/providers/${encodeURIComponent(provider)}/groups/${encodeURIComponent(groupName)}`,
    'POST',
    fetchImpl
  );
}

export async function removeProviderGroup(
  provider: string,
  groupName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(
    `${BASE}/providers/${encodeURIComponent(provider)}/groups/${encodeURIComponent(groupName)}`,
    'DELETE',
    fetchImpl
  );
}

export async function clearProvider(provider: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/providers/${encodeURIComponent(provider)}`, 'DELETE', fetchImpl);
}

export async function listAgePolicies(fetchImpl: typeof fetch = fetch): Promise<AgePolicy[]> {
  return getJson<AgePolicy[]>(`${BASE}/age-policies`, fetchImpl);
}

export async function addAgePolicyGroup(
  threshold: number,
  groupName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendJson<void>(`${BASE}/age-policies`, 'POST', { threshold, groupName }, fetchImpl);
}

export async function removeAgePolicyGroup(
  threshold: number,
  groupName: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(
    `${BASE}/age-policies/${encodeURIComponent(String(threshold))}/groups/${encodeURIComponent(groupName)}`,
    'DELETE',
    fetchImpl
  );
}
