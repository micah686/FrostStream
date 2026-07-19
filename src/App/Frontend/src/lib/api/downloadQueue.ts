export type DownloadJobState = string;
export type DownloadSourceKind = string;

export interface DownloadQueueJob {
  jobId: string;
  correlationId: string;
  state: DownloadJobState;
  sourceUrl: string;
  requestedBy: string | null;
  storageKey: string | null;
  sourceKind: DownloadSourceKind;
  priority: number;
  attemptMetadata: number;
  attemptDownload: number;
  attemptUpload: number;
  fileSizeBytes: number | null;
  contentHashXxh128: string | null;
  failureKind: string | null;
  failureCode: string | null;
  failureMessage: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
}

export interface DownloadQueueListResponse {
  items: DownloadQueueJob[];
  nextCursor: string | null;
  totalCount: number;
}

export interface QueueListParams {
  state?: DownloadJobState;
  stateGroup?: 'all' | 'active' | 'queued' | 'failed' | 'done' | 'cancelled';
  sourceKind?: DownloadSourceKind;
  requestedBy?: string;
  storageKey?: string;
  createdFrom?: string;
  createdTo?: string;
  q?: string;
  sort?: 'createdAt' | 'priority';
  limit?: number;
  cursor?: string;
}

export interface ProgressFrame {
  jobId: string;
  sequence: number;
  phase: string;
  percent: number | null;
  downloadedBytes: number | null;
  totalBytes: number | null;
  speed: string | null;
  etaSeconds: number | null;
  message: string | null;
}

export interface StateFrame {
  jobId: string;
  state: DownloadJobState;
  previousState: DownloadJobState;
  occurredAt: string;
}

export interface DownloadQueueHistoryEntry {
  id: number;
  messageId: string;
  operationKey: string;
  eventName: string;
  payloadJson: string | null;
  recordedAt: string;
}

const BASE = '/api/downloads/queue';

export const queueStreamUrl = (): string => `${BASE}/stream`;
export const jobProgressUrl = (jobId: string): string => `${BASE}/${jobId}/progress`;

export async function fetchJobHistory(
  jobId: string,
  fetchImpl: typeof fetch = fetch
): Promise<DownloadQueueHistoryEntry[]> {
  return getJson<DownloadQueueHistoryEntry[]>(`${BASE}/${jobId}/history`, fetchImpl);
}

/** Resolves a completed job to the media item it produced, or null when none is available
 *  (the job never produced media, or a later re-download of the same source superseded it). */
export async function fetchJobMediaGuid(jobId: string, fetchImpl: typeof fetch = fetch): Promise<string | null> {
  const response = await fetchImpl(`${BASE}/${jobId}/media`, { credentials: 'same-origin' });
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${BASE}/${jobId}/media failed with status ${response.status}.`));
  }
  const body = (await response.json()) as { mediaGuid: string };
  return body.mediaGuid;
}

export async function fetchQueue(
  params: QueueListParams = {},
  fetchImpl: typeof fetch = fetch
): Promise<DownloadQueueListResponse> {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      query.set(key, String(value));
    }
  }

  const suffix = query.toString();
  return getJson<DownloadQueueListResponse>(`${BASE}${suffix ? `?${suffix}` : ''}`, fetchImpl);
}

export async function cancelJob(jobId: string, reason?: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`${BASE}/${jobId}/cancel`, 'POST', reason ? { reason } : undefined, fetchImpl);
}

export async function setPriority(jobId: string, priority: number, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`${BASE}/${jobId}/priority`, 'PATCH', { priority }, fetchImpl);
}

export async function restartJob(jobId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`${BASE}/${jobId}/restart`, 'POST', undefined, fetchImpl);
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function send(url: string, method: string, body: unknown, fetchImpl: typeof fetch): Promise<Response> {
  const response = await fetchImpl(url, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
  return response;
}

async function describeError(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string; error?: string };
    return [problem.title, problem.detail, problem.error].filter(Boolean).join(' - ') || fallback;
  } catch {
    return fallback;
  }
}
