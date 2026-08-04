export type BackupJobStatus = 'queued' | 'running' | 'completed' | 'failed';

export type BackupType = 'full' | 'diff';

export type BackupJobKind = 'backup' | 'verify-quick' | 'verify-deep' | 'restore';

export interface BackupJob {
  jobId: string;
  kind: BackupJobKind;
  type: BackupType | null;
  status: BackupJobStatus;
  name: string | null;
  label: string | null;
  errorMessage: string | null;
  createdAt: string;
  completedAt: string | null;
  progress: string[];
}

export interface BackupInfo {
  label: string;
  type: BackupType;
  name: string | null;
  startedAt: string | null;
  completedAt: string | null;
  databaseSize: number | null;
  repositorySize: number | null;
  walStart: string | null;
  walStop: string | null;
  hasError: boolean;
  openBaoExportPresent: boolean;
}

export interface PitrWindow {
  earliest: string | null;
  latestApprox: string | null;
}

export interface BackupRepository {
  repositoryOk: boolean;
  statusMessage: string | null;
  backups: BackupInfo[];
  pitrWindow: PitrWindow;
}

const BASE = '/api/global/backups';

export async function startBackup(
  name?: string,
  type: BackupType = 'full',
  fetchImpl: typeof fetch = fetch
): Promise<BackupJob> {
  return sendJson<BackupJob>(BASE, { name: name?.trim() || null, type }, fetchImpl);
}

export async function listBackupJobs(fetchImpl: typeof fetch = fetch): Promise<BackupJob[]> {
  return getJson<BackupJob[]>(`${BASE}/jobs`, fetchImpl);
}

export async function getBackupJob(jobId: string, fetchImpl: typeof fetch = fetch): Promise<BackupJob> {
  return getJson<BackupJob>(`${BASE}/jobs/${encodeURIComponent(jobId)}`, fetchImpl);
}

export async function listBackups(fetchImpl: typeof fetch = fetch): Promise<BackupRepository> {
  return getJson<BackupRepository>(BASE, fetchImpl);
}

/** Queues a verification job. Quick verify checks the whole repository; deep verify test-restores one backup. */
export async function verifyBackup(
  label: string | null,
  deep: boolean,
  fetchImpl: typeof fetch = fetch
): Promise<BackupJob> {
  return sendJson<BackupJob>(`${BASE}/verify`, { label, deep }, fetchImpl);
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendJson<T>(url: string, body: unknown, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `POST ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
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
