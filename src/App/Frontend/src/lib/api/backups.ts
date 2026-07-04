export type BackupJobStatus = 'queued' | 'running' | 'completed' | 'failed';

export type BackupMode = 'snapshot' | 'full' | 'wal-archive';

export interface BackupJob {
  jobId: string;
  status: BackupJobStatus;
  archivePath: string | null;
  errorMessage: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface BackupSummary {
  archivePath: string;
  createdAt: string | null;
  mediaIncluded: boolean;
  schemaVersion: number;
  mode: string;
}

export interface VerifyBackupResult {
  success: boolean;
  errorMessage: string | null;
}

export interface RestorePlan {
  preflightOk: boolean;
  restoreCommand: string;
  errorMessage: string | null;
}

const BASE = '/api/admin/backups';

export async function startBackup(
  name?: string,
  mode: BackupMode = 'snapshot',
  fetchImpl: typeof fetch = fetch
): Promise<BackupJob> {
  return sendJson<BackupJob>(BASE, { name: name?.trim() || null, mode }, fetchImpl);
}

export async function listBackupJobs(fetchImpl: typeof fetch = fetch): Promise<BackupJob[]> {
  return getJson<BackupJob[]>(`${BASE}/jobs`, fetchImpl);
}

export async function getBackupJob(jobId: string, fetchImpl: typeof fetch = fetch): Promise<BackupJob> {
  return getJson<BackupJob>(`${BASE}/jobs/${encodeURIComponent(jobId)}`, fetchImpl);
}

export async function listBackups(fetchImpl: typeof fetch = fetch): Promise<BackupSummary[]> {
  return getJson<BackupSummary[]>(BASE, fetchImpl);
}

export async function verifyBackup(archivePath: string, fetchImpl: typeof fetch = fetch): Promise<VerifyBackupResult> {
  return sendJson<VerifyBackupResult>(`${BASE}/verify`, { archivePath }, fetchImpl);
}

export async function buildRestorePlan(archivePath: string, fetchImpl: typeof fetch = fetch): Promise<RestorePlan> {
  return sendJson<RestorePlan>(`${BASE}/restore-plan`, { archivePath }, fetchImpl);
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
