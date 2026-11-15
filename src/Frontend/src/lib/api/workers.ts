export interface WorkerInfo {
  workerId: string;
  name: string;
  tags: string[];
  incomingRoot: string;
  lastOnline: string;
  maxActiveJobs: number;
  activeJobs: number;
}

export async function setWorkerCapacity(workerId: string, maxActiveJobs: number, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl(`/api/admin/workers/${encodeURIComponent(workerId)}/capacity`, {
    method: 'PUT', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ workerId, maxActiveJobs })
  });
  if (!response.ok) throw new Error(`Could not update worker capacity (${response.status}).`);
  const payload = await response.json();
  if (!payload.success) throw new Error(payload.error ?? 'Could not update worker capacity.');
}

export async function listWorkers(tag?: string, fetchImpl: typeof fetch = fetch): Promise<WorkerInfo[]> {
  const query = tag?.trim() ? `?tag=${encodeURIComponent(tag.trim())}` : '';
  const response = await fetchImpl(`/api/admin/workers${query}`, { credentials: 'same-origin' });
  if (!response.ok) throw new Error(`Could not load workers (${response.status}).`);
  const payload = await response.json();
  return payload.workers ?? [];
}
