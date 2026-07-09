export interface ImportSessionSubmission {
  storageKey: string;
  workerTag?: string;
  requestedBy?: string;
}

export type ImportSessionStatus =
  | 'scanning'
  | 'scanFailed'
  | 'reviewing'
  | 'committing'
  | 'completed'
  | 'completedWithFailures'
  | 'cancelled';

export type ImportSessionSourceKind = 'workerIncoming' | 'storageBackend';
export type ImportSessionItemStatus =
  | 'discovered'
  | 'probed'
  | 'approved'
  | 'hashing'
  | 'uploading'
  | 'finalizing'
  | 'imported'
  | 'alreadyImported'
  | 'failed';
export type ImportSessionItemMetadataState = 'incomplete' | 'ready' | 'edited' | 'placeholderAccepted';
export type ImportSessionBulkAction = 'acceptPlaceholders' | 'exclude' | 'include' | 'resetFailed';

export interface ImportSession {
  sessionId: string;
  correlationId: string;
  status: ImportSessionStatus;
  sourceKind: ImportSessionSourceKind;
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
  excluded: boolean;
  status: ImportSessionItemStatus;
  attempt: number;
  errorCode?: string | null;
  errorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface ImportSessionListResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  items: ImportSession[];
  nextSessionId?: string | null;
}

export interface ImportSessionItemsListResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  items: ImportSessionItem[];
  nextItemId?: string | null;
  totalCount: number;
}

export interface ImportSessionItemPatch {
  title?: string;
  provider?: string;
  sourceMediaId?: string;
  sourceUrl?: string;
}

export interface ImportSessionItemPatchResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  item?: ImportSessionItem | null;
  session?: ImportSession | null;
}

export interface ImportSessionItemsBulkResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  affectedCount: number;
  session?: ImportSession | null;
}

export interface ImportSessionMappingApplyResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  matchedCount: number;
  unmatchedCount: number;
  session?: ImportSession | null;
}

export interface ImportSessionEnrichResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  queuedCount: number;
  session?: ImportSession | null;
}

export interface ImportSessionCommitResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  approvedCount: number;
  session?: ImportSession | null;
}

export interface ImportSessionRetryFailedResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  resetCount: number;
  session?: ImportSession | null;
}

export interface ImportSessionCancelResponse {
  success: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  session?: ImportSession | null;
}

const SESSIONS_BASE = '/api/global/imports/sessions';

export async function createImportSession(
  submission: ImportSessionSubmission & { subPath?: string },
  fetchImpl: typeof fetch = fetch
): Promise<ImportSession> {
  const response = await fetchImpl(SESSIONS_BASE, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      sourceKind: 'workerIncoming',
      storageKey: submission.storageKey,
      workerTag: submission.workerTag || undefined,
      subPath: submission.subPath || undefined,
      requestedBy: submission.requestedBy || undefined
    })
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `Import session create failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSession;
}

export async function listImportSessions(fetchImpl: typeof fetch = fetch): Promise<ImportSessionListResponse> {
  const response = await fetchImpl(SESSIONS_BASE, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import sessions request failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionListResponse;
}

export async function getImportSession(sessionId: string, fetchImpl: typeof fetch = fetch): Promise<ImportSession> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}`, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import session request failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSession;
}

export async function listImportSessionItems(
  sessionId: string,
  params: {
    status?: ImportSessionItemStatus;
    metadataState?: ImportSessionItemMetadataState;
    search?: string;
    afterItemId?: string;
    limit?: number;
  } = {},
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionItemsListResponse> {
  const query = new URLSearchParams();
  if (params.status) query.set('status', params.status);
  if (params.metadataState) query.set('metadataState', params.metadataState);
  if (params.search) query.set('search', params.search);
  if (params.afterItemId) query.set('afterItemId', params.afterItemId);
  if (params.limit) query.set('limit', String(params.limit));

  const suffix = query.toString();
  const response = await fetchImpl(
    `${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/items${suffix ? `?${suffix}` : ''}`,
    { credentials: 'same-origin' }
  );
  if (!response.ok) {
    throw new Error(await describeError(response, `Import session items request failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionItemsListResponse;
}

export async function patchImportSessionItem(
  sessionId: string,
  itemId: string,
  patch: ImportSessionItemPatch,
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionItemPatchResponse> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/items/${encodeURIComponent(itemId)}`, {
    method: 'PATCH',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(patch)
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import item update failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionItemPatchResponse;
}

export async function bulkImportSessionItems(
  sessionId: string,
  request: {
    action: ImportSessionBulkAction;
    itemIds?: string[];
    status?: ImportSessionItemStatus;
    metadataState?: ImportSessionItemMetadataState;
    search?: string;
  },
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionItemsBulkResponse> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/items/bulk`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(request)
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import bulk action failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionItemsBulkResponse;
}

export async function applyImportSessionMapping(
  sessionId: string,
  file: File,
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionMappingApplyResponse> {
  const form = new FormData();
  form.append('file', file);
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/mapping`, {
    method: 'POST',
    credentials: 'same-origin',
    body: form
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import mapping upload failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionMappingApplyResponse;
}

export async function enrichImportSession(
  sessionId: string,
  itemIds?: string[],
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionEnrichResponse> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/enrich`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ itemIds: itemIds && itemIds.length > 0 ? itemIds : undefined })
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import enrichment failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionEnrichResponse;
}

export async function commitImportSession(
  sessionId: string,
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionCommitResponse> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/commit`, {
    method: 'POST',
    credentials: 'same-origin'
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import commit failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionCommitResponse;
}

export async function retryFailedImportSession(
  sessionId: string,
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionRetryFailedResponse> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/retry-failed`, {
    method: 'POST',
    credentials: 'same-origin'
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import retry failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionRetryFailedResponse;
}

export async function cancelImportSession(
  sessionId: string,
  fetchImpl: typeof fetch = fetch
): Promise<ImportSessionCancelResponse> {
  const response = await fetchImpl(`${SESSIONS_BASE}/${encodeURIComponent(sessionId)}/cancel`, {
    method: 'POST',
    credentials: 'same-origin'
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `Import cancel failed with status ${response.status}.`));
  }
  return (await response.json()) as ImportSessionCancelResponse;
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
