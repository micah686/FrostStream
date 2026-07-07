export interface LocalMediaImportSubmission {
  storageKey: string;
  workerTag?: string;
  requestedBy?: string;
}

export interface LocalMediaImportReceipt {
  batchId: string;
  correlationId: string;
}

const BASE = '/api/global/imports';

export async function submitLocalMediaImport(
  submission: LocalMediaImportSubmission,
  fetchImpl: typeof fetch = fetch
): Promise<LocalMediaImportReceipt> {
  const response = await fetchImpl(`${BASE}/local-media`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      storageKey: submission.storageKey,
      workerTag: submission.workerTag || undefined,
      requestedBy: submission.requestedBy || undefined
    })
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `Import request failed with status ${response.status}.`));
  }
  return (await response.json()) as LocalMediaImportReceipt;
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
