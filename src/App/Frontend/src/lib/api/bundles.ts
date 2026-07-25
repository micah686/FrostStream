import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export type GranteeType = 'user' | 'group';

export interface CatalogEntry {
  id: string;
  bundle: string;
}

export interface DirectoryEntry {
  type: GranteeType;
  id: string;
  name: string;
  description?: string | null;
}

export interface BundleMemberPolicy {
  policyId: string;
  name: string;
  enabled: boolean;
  syncStatus: 'Pending' | 'Synced' | 'Failed';
}

export interface BundleView {
  id: string;
  systemOwned: boolean;
  endpoints: string[];
  endpointCount: number;
  policyCount: number;
  memberPolicies: BundleMemberPolicy[];
}

const BASE = '/api/global/access-control';

export async function listBundles(fetchImpl: typeof fetch = fetch): Promise<BundleView[]> {
  return getJson<BundleView[]>(`${BASE}/bundles`, fetchImpl);
}

export async function listCatalog(fetchImpl: typeof fetch = fetch): Promise<CatalogEntry[]> {
  return getJson<CatalogEntry[]>(`${BASE}/catalog`, fetchImpl);
}

export async function createRuntimeBundle(
  request: { id: string; name: string; cloneFrom?: string | null; endpoints?: string[] },
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendJson<void>(`${BASE}/bundles`, 'POST', request, fetchImpl);
}

export async function replaceBundleEndpoints(
  bundleId: string,
  endpoints: string[],
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendJson<void>(`${BASE}/bundles/${encodeURIComponent(bundleId)}/endpoints`, 'PUT', { endpoints }, fetchImpl);
}

export async function deleteRuntimeBundle(bundleId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/bundles/${encodeURIComponent(bundleId)}`, 'DELETE', fetchImpl);
}

export async function searchDirectory(
  type: GranteeType,
  query: string,
  fetchImpl: typeof fetch = fetch
): Promise<DirectoryEntry[]> {
  const params = new URLSearchParams({ type, q: query });
  return getJson<DirectoryEntry[]>(`${BASE}/directory?${params}`, fetchImpl);
}
