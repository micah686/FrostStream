export interface WorkerInfo {
  workerId: string;
  name: string;
  tags: string[];
  incomingRoot: string;
  lastOnline: string;
}

export async function listWorkers(tag?: string, fetchImpl: typeof fetch = fetch): Promise<WorkerInfo[]> {
  const query = tag?.trim() ? `?tag=${encodeURIComponent(tag.trim())}` : '';
  const response = await fetchImpl(`/api/admin/workers${query}`, { credentials: 'same-origin' });
  if (!response.ok) throw new Error(`Could not load workers (${response.status}).`);
  const payload = await response.json();
  return payload.workers ?? [];
}
