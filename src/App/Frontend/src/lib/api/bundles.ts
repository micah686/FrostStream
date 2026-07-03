import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export type GranteeType = 'user' | 'group';

export interface CatalogEntry {
  id: string;
  bundle: string;
}

export interface BundleGrant {
  type: GranteeType;
  id: string;
}

export interface BundleView {
  id: string;
  systemOwned: boolean;
  endpoints: string[];
  grants: BundleGrant[];
}

const BASE = '/api/management';

export async function listBundles(fetchImpl: typeof fetch = fetch): Promise<BundleView[]> {
  return getJson<BundleView[]>(`${BASE}/bundles`, fetchImpl);
}

export async function listCatalog(fetchImpl: typeof fetch = fetch): Promise<CatalogEntry[]> {
  return getJson<CatalogEntry[]>(`${BASE}/catalog`, fetchImpl);
}

export async function createRuntimeBundle(
  request: { id: string; endpoints: string[] },
  fetchImpl: typeof fetch = fetch
): Promise<BundleView> {
  return sendJson<BundleView>(`${BASE}/bundles`, 'POST', request, fetchImpl);
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

export async function addBundleGrant(
  bundleId: string,
  grant: BundleGrant,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendJson<void>(`${BASE}/bundles/${encodeURIComponent(bundleId)}/grants`, 'POST', grant, fetchImpl);
}

export async function revokeBundleGrant(
  bundleId: string,
  grant: BundleGrant,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  const query = new URLSearchParams({ type: grant.type, id: grant.id });
  await sendEmpty(`${BASE}/bundles/${encodeURIComponent(bundleId)}/grants?${query}`, 'DELETE', fetchImpl);
}
