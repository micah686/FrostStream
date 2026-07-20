export type DownloadJobStatus =
  | 'Queued'
  | 'Running'
  | 'Stopping'
  | 'Stopped'
  | 'Compensating'
  | 'Completed'
  | 'CompletedWithWarnings'
  | 'Failed'
  | 'AlreadyDownloaded'
  | 'Ignored';

export type DownloadStage =
  | 'None'
  | 'Metadata'
  | 'DuplicateCheck'
  | 'WaitingForWorker'
  | 'MediaAcquire'
  | 'PrimaryMediaUpload'
  | 'MetaSidecarUpload'
  | 'InfoJsonUpload'
  | 'ThumbnailUpload'
  | 'CaptionUpload'
  | 'RichMetadataWrite'
  | 'Finalize'
  | 'Cleanup'
  | 'Compensation';

export type DownloadStageStatus =
  | 'Pending'
  | 'Running'
  | 'RetryWaiting'
  | 'Succeeded'
  | 'Skipped'
  | 'Warning'
  | 'Failed'
  | 'Stopped';

/** The pre-V2 state is returned temporarily for old history/debugging data only. */
export type DownloadJobState = string;
export type DownloadSourceKind = string;

export interface DownloadQueueJob {
  jobId: string;
  correlationId: string;
  status: DownloadJobStatus;
  stage: DownloadStage;
  stageStatus: DownloadStageStatus;
  runId: string | null;
  runNumber: number;
  attempt: number;
  maxAttempts: number;
  artifactKey: string | null;
  warningCount: number;
  /** @deprecated V2 clients use status/stage. */
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
  status?: DownloadJobStatus;
  stateGroup?: 'all' | 'active' | 'queued' | 'failed' | 'done' | 'stopped';
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
  runId: string | null;
  stage: DownloadStage | null;
  attempt: number;
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
  status: DownloadJobStatus;
  previousStatus: DownloadJobStatus;
  stage: DownloadStage;
  stageStatus: DownloadStageStatus;
  runId: string | null;
  runNumber: number;
  attempt: number;
  artifactKey: string | null;
  warningCount: number;
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

export async function stopJob(jobId: string, reason?: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`${BASE}/${jobId}/stop`, 'POST', reason ? { reason } : undefined, fetchImpl);
}

export async function setPriority(jobId: string, priority: number, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`${BASE}/${jobId}/priority`, 'PATCH', { priority }, fetchImpl);
}

export async function startJob(jobId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`${BASE}/${jobId}/start`, 'POST', undefined, fetchImpl);
}

export async function stopGroup(
  correlationId: string,
  reason?: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await send(`/api/downloads/groups/${correlationId}/stop`, 'POST', reason ? { reason } : undefined, fetchImpl);
}

export async function startGroup(correlationId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`/api/downloads/groups/${correlationId}/start`, 'POST', undefined, fetchImpl);
}

/** Clears only the persistent provider circuit. Jobs remain Stopped/Failed until Start is pressed. */
export async function clearProviderCircuit(provider: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await send(`/api/downloads/providers/${encodeURIComponent(provider)}/circuit/clear`, 'POST', undefined, fetchImpl);
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
