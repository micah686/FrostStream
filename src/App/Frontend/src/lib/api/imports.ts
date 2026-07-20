export interface ImportSessionSubmission {
  storageKey: string;
  workerTag?: string;
  subPath?: string;
  requestedBy?: string;
}

export type ImportSessionStatus =
  | 'scanning' | 'scanFailed' | 'reviewing' | 'committing'
  | 'completed' | 'completedWithFailures' | 'cancelled';
export type ImportSessionItemStatus =
  | 'discovered' | 'probed' | 'approved' | 'hashing' | 'uploading'
  | 'finalizing' | 'imported' | 'alreadyImported' | 'failed';
export type ImportSessionItemMetadataState = 'incomplete' | 'ready' | 'edited' | 'placeholderAccepted';
export type ImportSessionItemMetadataSource = 'placeholder' | 'nfo' | 'infoJson' | 'ytDlp' | 'manualMapping';
export type ImportSessionMetadataFetchState = 'notAttempted' | 'queued' | 'succeeded' | 'failed';
export type ImportSessionBulkAction = 'acceptPlaceholders' | 'exclude' | 'include' | 'resetFailed';

export interface ImportSession {
  sessionId: string;
  correlationId: string;
  status: ImportSessionStatus;
  sourceKind: 'workerIncoming' | 'storageBackend';
  sourceRoot: string;
  subPath?: string | null;
  storageKey: string;
  workerTag?: string | null;
  requestedBy?: string | null;
  totalItems: number;
  probedItems: number;
  readyItems: number;
  incompleteItems: number;
  excludedItems: number;
  approvedItems: number;
  importedItems: number;
  alreadyImportedItems: number;
  failedItems: number;
  maxParallelItems: number;
  errorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface ImportSessionItem {
  itemId: string;
  sessionId: string;
  relativePath: string;
  fileName: string;
  fileSizeBytes: number;
  fileMtime?: string | null;
  provider?: string | null;
  sourceMediaId?: string | null;
  sourceUrl?: string | null;
  title?: string | null;
  metadataState: ImportSessionItemMetadataState;
  metadataSource: ImportSessionItemMetadataSource;
  metadataFetchState: ImportSessionMetadataFetchState;
  metadataFetchAttempt: number;
  metadataFetchMessage?: string | null;
  metadataJson?: string | null;
  hasNfo: boolean;
  hasInfoJson: boolean;
  excluded: boolean;
  status: ImportSessionItemStatus;
  attempt: number;
  errorCode?: string | null;
  errorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface ImportSessionListResponse { success: boolean; items: ImportSession[]; nextSessionId?: string | null; }
export interface ImportSessionItemsListResponse { success: boolean; items: ImportSessionItem[]; nextItemId?: string | null; totalCount: number; }
export interface ImportSessionActionResponse { success: boolean; errorCode?: string | null; errorMessage?: string | null; session?: ImportSession | null; }
export interface ImportSessionItemsBulkResponse extends ImportSessionActionResponse { affectedCount: number; }
export interface ImportSessionMappingApplyResponse extends ImportSessionActionResponse { matchedCount: number; unmatchedCount: number; }
export interface ImportSessionEnrichResponse extends ImportSessionActionResponse { queuedCount: number; }
export interface ImportSessionMetadataRefreshResponse extends ImportSessionActionResponse { checkedCount: number; foundCount: number; }
export interface ImportSessionCommitResponse extends ImportSessionActionResponse { approvedCount: number; }
export interface ImportSessionRetryFailedResponse extends ImportSessionActionResponse { resetCount: number; }
export interface ImportSessionItemPatchResponse extends ImportSessionActionResponse { item?: ImportSessionItem | null; }

export interface ImportYtDlpOptions {
  proxyUrl?: string;
  username?: string;
  password?: string;
  twoFactorCode?: string;
  videoPassword?: string;
  skipCertificateChecks?: boolean;
  allowLegacyConnections?: boolean;
  extraHttpHeaders?: string[];
  sleepBetweenRequestsSeconds: number;
}

export interface BrowseIncomingResponse { success: boolean; subPath: string; directories: string[]; }

const SESSIONS_BASE = '/api/global/imports/sessions';

const enumValues: Record<string, Record<string, string>> = {
  status: {
    Scanning: 'scanning',
    ScanFailed: 'scanFailed',
    Reviewing: 'reviewing',
    Committing: 'committing',
    Completed: 'completed',
    CompletedWithFailures: 'completedWithFailures',
    Cancelled: 'cancelled',
    Discovered: 'discovered',
    Probed: 'probed',
    Approved: 'approved',
    Hashing: 'hashing',
    Uploading: 'uploading',
    Finalizing: 'finalizing',
    Imported: 'imported',
    AlreadyImported: 'alreadyImported',
    Failed: 'failed'
  },
  sourceKind: {
    WorkerIncoming: 'workerIncoming',
    StorageBackend: 'storageBackend'
  },
  metadataState: {
    Incomplete: 'incomplete',
    Ready: 'ready',
    Edited: 'edited',
    PlaceholderAccepted: 'placeholderAccepted'
  },
  metadataSource: {
    Placeholder: 'placeholder',
    Nfo: 'nfo',
    InfoJson: 'infoJson',
    YtDlp: 'ytDlp',
    ManualMapping: 'manualMapping'
  },
  metadataFetchState: {
    NotAttempted: 'notAttempted',
    Queued: 'queued',
    Succeeded: 'succeeded',
    Failed: 'failed'
  }
};

function normalizeImportEnums<T>(value: T): T {
  if (Array.isArray(value)) return value.map((item) => normalizeImportEnums(item)) as T;
  if (!value || typeof value !== 'object') return value;
  const result: Record<string, unknown> = {};
  for (const [key, raw] of Object.entries(value)) {
    const normalized = normalizeImportEnums(raw);
    result[key] = typeof normalized === 'string' ? enumValues[key]?.[normalized] ?? normalized : normalized;
  }
  return result as T;
}

async function requestJson<T>(url: string, init: RequestInit = {}, fetchImpl: typeof fetch = fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin', ...init });
  if (!response.ok) throw new Error(await describeError(response, `Import request failed with status ${response.status}.`));
  return normalizeImportEnums((await response.json()) as T);
}

export function createImportSession(submission: ImportSessionSubmission, fetchImpl: typeof fetch = fetch): Promise<ImportSession> {
  return requestJson(SESSIONS_BASE, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ sourceKind: 'workerIncoming', ...submission }) }, fetchImpl);
}

export function listImportSessions(fetchImpl: typeof fetch = fetch): Promise<ImportSessionListResponse> {
  return requestJson(SESSIONS_BASE, {}, fetchImpl);
}

export function getImportSession(sessionId: string, fetchImpl: typeof fetch = fetch): Promise<ImportSession> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}`, {}, fetchImpl);
}

export function listImportSessionItems(
  sessionId: string,
  params: { status?: ImportSessionItemStatus; metadataState?: ImportSessionItemMetadataState; search?: string; included?: boolean; afterItemId?: string; limit?: number } = {},
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionItemsListResponse> {
  const query = new URLSearchParams();
  if (params.status) query.set('status', params.status);
  if (params.metadataState) query.set('metadataState', params.metadataState);
  if (params.search) query.set('search', params.search);
  if (params.included !== undefined) query.set('included', String(params.included));
  if (params.afterItemId) query.set('afterItemId', params.afterItemId);
  if (params.limit) query.set('limit', String(params.limit));
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/items?${query}`, {}, fetchImpl);
}

export async function listAllImportSessionItems(
  sessionId: string,
  params: { status?: ImportSessionItemStatus; metadataState?: ImportSessionItemMetadataState; search?: string; included?: boolean } = {},
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionItem[]> {
  const items: ImportSessionItem[] = [];
  let afterItemId: string | undefined;
  do {
    const page = await listImportSessionItems(sessionId, { ...params, afterItemId, limit: 200 }, fetchImpl);
    items.push(...page.items);
    afterItemId = page.nextItemId || undefined;
  } while (afterItemId);
  return items;
}

export function patchImportSessionItem(sessionId: string, itemId: string, patch: { title?: string; provider?: string; sourceMediaId?: string; sourceUrl?: string }, fetchImpl: typeof fetch = fetch): Promise<ImportSessionItemPatchResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/items/${encodeURIComponent(itemId)}`, { method: 'PATCH', headers: { 'content-type': 'application/json' }, body: JSON.stringify(patch) }, fetchImpl);
}

export function bulkImportSessionItems(sessionId: string, request: { action: ImportSessionBulkAction; itemIds?: string[]; status?: ImportSessionItemStatus; metadataState?: ImportSessionItemMetadataState; search?: string }, fetchImpl: typeof fetch = fetch): Promise<ImportSessionItemsBulkResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/items/bulk`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(request) }, fetchImpl);
}

export async function applyImportSessionMapping(sessionId: string, file: File, fetchImpl: typeof fetch = fetch): Promise<ImportSessionMappingApplyResponse> {
  const form = new FormData();
  form.append('file', file);
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/mapping`, { method: 'POST', body: form }, fetchImpl);
}

export function mappingTemplateUrl(sessionId: string): string {
  return `${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/mapping-template`;
}

export function enrichImportSession(sessionId: string, itemIds: string[] | undefined, options: ImportYtDlpOptions, fetchImpl: typeof fetch = fetch): Promise<ImportSessionEnrichResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/enrich`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ itemIds: itemIds?.length ? itemIds : undefined, options }) }, fetchImpl);
}

export function refreshImportSessionMetadata(sessionId: string, itemIds?: string[], fetchImpl: typeof fetch = fetch): Promise<ImportSessionMetadataRefreshResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/metadata-refresh`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ itemIds: itemIds?.length ? itemIds : undefined }) }, fetchImpl);
}

export function commitImportSession(sessionId: string, fetchImpl: typeof fetch = fetch): Promise<ImportSessionCommitResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/commit`, { method: 'POST' }, fetchImpl);
}

export function retryFailedImportSession(sessionId: string, fetchImpl: typeof fetch = fetch): Promise<ImportSessionRetryFailedResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/retry-failed`, { method: 'POST' }, fetchImpl);
}

export function cancelImportSession(sessionId: string, fetchImpl: typeof fetch = fetch): Promise<ImportSessionActionResponse> {
  return requestJson(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/cancel`, { method: 'POST' }, fetchImpl);
}

export function browseImportIncoming(path?: string, workerTag?: string, fetchImpl: typeof fetch = fetch): Promise<BrowseIncomingResponse> {
  const query = new URLSearchParams();
  if (path) query.set('path', path);
  if (workerTag) query.set('workerTag', workerTag);
  return requestJson(`/api/global/imports/incoming/browse?${query}`, {}, fetchImpl);
}

async function describeError(response: Response, fallback: string): Promise<string> {
  const text = await response.text();
  if (!text) return fallback;
  try {
    const problem = JSON.parse(text) as { title?: string; detail?: string; error?: string; errors?: Record<string, string[]> };
    const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : '';
    return [problem.title, problem.detail, problem.error, validation].filter(Boolean).join(' - ') || text;
  } catch { return text; }
}
