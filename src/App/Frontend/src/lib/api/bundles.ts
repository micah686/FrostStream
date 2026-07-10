import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export type GranteeType = 'user' | 'group';

export interface CatalogEntry {
  id: string;
  bundle: string;
}

export interface BundleGrant {
  type: GranteeType;
  id: string;
  /** True for the seeded lock-out guard grant (bootstrap admin group on the `all` bundle); it cannot be revoked. */
  locked?: boolean;
  /** Friendly name resolved from the identity provider (user grants only; the id is the opaque subject UUID). */
  displayName?: string | null;
}

export interface DirectoryEntry {
  type: GranteeType;
  id: string;
  name: string;
  description?: string | null;
}

export interface BundleView {
  id: string;
  systemOwned: boolean;
  endpoints: string[];
  grants: BundleGrant[];
}

const BASE = '/api/global/management';

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

export async function searchDirectory(
  type: GranteeType,
  query: string,
  fetchImpl: typeof fetch = fetch
): Promise<DirectoryEntry[]> {
  const params = new URLSearchParams({ type, q: query });
  return getJson<DirectoryEntry[]>(`${BASE}/directory?${params}`, fetchImpl);
}

export async function revokeBundleGrant(
  bundleId: string,
  grant: BundleGrant,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  const query = new URLSearchParams({ type: grant.type, id: grant.id });
  await sendEmpty(`${BASE}/bundles/${encodeURIComponent(bundleId)}/grants?${query}`, 'DELETE', fetchImpl);
}
