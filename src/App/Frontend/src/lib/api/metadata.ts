export type OrphanKind = 'media_without_metadata' | 'metadata_without_media';
export type OrphanState = 'detected' | 'moved' | 'move_failed' | 'delete_failed' | 'finalized' | 'resolved';

export interface OrphanCleanupItem {
  id: number;
  kind: OrphanKind;
  state: OrphanState;
  storageKey: string;
  originalStoragePath: string;
  orphanStoragePath: string | null;
  mediaGuid: string | null;
  detectedAt: string;
  lastSeenAt: string;
  deleteAfter: string;
  movedAt: string | null;
  finalizedAt: string | null;
  resolvedAt: string | null;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface OrphanCleanupPolicy {
  enabled: boolean;
  fileMoveAfterDays: number;
  filePurgeAfterDays: number;
  metadataDeleteAfterDays: number;
  updatedBy: string | null;
  updatedAt: string | null;
  lastRunAt: string | null;
  lastMovedCount: number;
  lastDeletedFilesCount: number;
  lastDeletedMetadataCount: number;
}

export interface OrphanCleanupPolicyUpdate {
  enabled: boolean;
  fileMoveAfterDays: number;
  filePurgeAfterDays: number;
  metadataDeleteAfterDays: number;
}

export interface WatchedAutoDeletePolicy {
  enabled: boolean;
  deleteAfterDays: number;
  maxDeletionsPerRun: number;
  updatedBy: string | null;
  updatedAt: string | null;
  lastRunAt: string | null;
  lastDeletedCount: number;
  lastFailedCount: number;
}

export interface WatchedAutoDeletePolicyUpdate {
  enabled: boolean;
  deleteAfterDays: number;
  maxDeletionsPerRun: number;
}

export interface WatchedAutoDeleteCleanupResult {
  policyEnabled: boolean;
  cutoff: string | null;
  candidatesFound: number;
  deletedCount: number;
  failedCount: number;
  filesDeleted: number;
}

export interface MediaDeleteResult {
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  filesDeleted: number;
  mediaRemoved: boolean;
}

export interface MetadataVersion {
  mediaGuid: string;
  versionNum: number;
  storageKey: string;
  storagePath: string;
  contentHashXxh128: string;
  ingestOrigin: string;
}

export interface MetadataVersionsResponse {
  totalCount: number;
  versions: MetadataVersion[];
}

export interface ListOrphansOptions {
  kind?: OrphanKind;
  state?: OrphanState;
  pageSize?: number;
  page?: number;
}

const BASE = '/api/global/metadata';

export async function triggerReindex(fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/reindex`, 'POST', fetchImpl);
}

export async function listOrphans(
  options: ListOrphansOptions = {},
  fetchImpl: typeof fetch = fetch
): Promise<OrphanCleanupItem[]> {
  const query = new URLSearchParams();
  if (options.kind) query.set('kind', options.kind);
  if (options.state) query.set('state', options.state);
  if (options.pageSize) query.set('pageSize', String(options.pageSize));
  if (options.page) query.set('page', String(options.page));
  const suffix = query.size > 0 ? `?${query}` : '';
  return getJson<OrphanCleanupItem[]>(`${BASE}/orphans${suffix}`, fetchImpl);
}

export async function restoreOrphanFile(id: number, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/orphans/${id}/restore-file`, 'POST', fetchImpl);
}

export async function restoreOrphanMetadata(id: number, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/orphans/${id}/restore-metadata`, 'POST', fetchImpl);
}

export async function getOrphanCleanupPolicy(fetchImpl: typeof fetch = fetch): Promise<OrphanCleanupPolicy> {
  return getJson<OrphanCleanupPolicy>(`${BASE}/orphan-cleanup-policy`, fetchImpl);
}

export async function updateOrphanCleanupPolicy(
  request: OrphanCleanupPolicyUpdate,
  fetchImpl: typeof fetch = fetch
): Promise<OrphanCleanupPolicy> {
  return sendJson<OrphanCleanupPolicy>(`${BASE}/orphan-cleanup-policy`, 'PUT', request, fetchImpl);
}

export async function getWatchedAutoDeletePolicy(fetchImpl: typeof fetch = fetch): Promise<WatchedAutoDeletePolicy> {
  return getJson<WatchedAutoDeletePolicy>(`${BASE}/watched-auto-delete`, fetchImpl);
}

export async function updateWatchedAutoDeletePolicy(
  request: WatchedAutoDeletePolicyUpdate,
  fetchImpl: typeof fetch = fetch
): Promise<WatchedAutoDeletePolicy> {
  return sendJson<WatchedAutoDeletePolicy>(`${BASE}/watched-auto-delete`, 'PUT', request, fetchImpl);
}

export async function runWatchedAutoDelete(fetchImpl: typeof fetch = fetch): Promise<WatchedAutoDeleteCleanupResult> {
  return sendJson<WatchedAutoDeleteCleanupResult>(`${BASE}/watched-auto-delete/run`, 'POST', undefined, fetchImpl);
}

export async function deleteMedia(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<MediaDeleteResult> {
  return sendJson<MediaDeleteResult>(`${BASE}/${encodeURIComponent(mediaGuid)}`, 'DELETE', undefined, fetchImpl);
}

export async function deleteMediaForStorageKey(
  mediaGuid: string,
  storageKey: string,
  fetchImpl: typeof fetch = fetch
): Promise<MediaDeleteResult> {
  return sendJson<MediaDeleteResult>(
    `${BASE}/${encodeURIComponent(mediaGuid)}/storage/${encodeURIComponent(storageKey)}`,
    'DELETE',
    undefined,
    fetchImpl
  );
}

export async function getMetadataVersions(
  mediaGuid: string,
  fetchImpl: typeof fetch = fetch
): Promise<MetadataVersionsResponse> {
  return getJson<MetadataVersionsResponse>(`/api/metadata/${encodeURIComponent(mediaGuid)}/versions`, fetchImpl);
}

export async function getMetadataVersionCount(
  mediaGuid: string,
  fetchImpl: typeof fetch = fetch
): Promise<number> {
  return getJson<number>(`/api/metadata/${encodeURIComponent(mediaGuid)}/versions?countOnly=true`, fetchImpl);
}

export function orphanKindLabel(kind: OrphanKind | string): string {
  switch (kind) {
    case 'media_without_metadata':
      return 'Orphaned file';
    case 'metadata_without_media':
      return 'Orphaned metadata';
    default:
      return kind;
  }
}

export function orphanStateLabel(state: OrphanState | string): string {
  switch (state) {
    case 'detected':
      return 'Detected';
    case 'moved':
      return 'Moved';
    case 'move_failed':
      return 'Move failed';
    case 'delete_failed':
      return 'Delete failed';
    case 'finalized':
      return 'Finalized';
    case 'resolved':
      return 'Resolved';
    default:
      return state;
  }
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendJson<T>(url: string, method: string, body: unknown, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, {
    method,
    credentials: 'same-origin',
    ...(body === undefined
      ? {}
      : { headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendEmpty(url: string, method: string, fetchImpl: typeof fetch): Promise<void> {
  const response = await fetchImpl(url, { method, credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
}

async function describeError(response: Response, fallback: string): Promise<string> {
  const text = await response.text();
  if (!text) {
    return fallback;
  }

  try {
    const problem = JSON.parse(text) as { title?: string; detail?: string; error?: string; errors?: Record<string, string[]> };
    const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : '';
    return [problem.title, problem.detail, problem.error, validation].filter(Boolean).join(' - ') || text || fallback;
  } catch {
    return text;
  }
}
