export interface LocalMediaImportSubmission {
  manifest: File;
  sourceRoot: string;
  storageKey: string;
  requestedBy?: string;
}

export interface LocalMediaImportReceipt {
  batchId: string;
  correlationId: string;
}

const BASE = '/api/imports';

export async function submitLocalMediaImport(
  submission: LocalMediaImportSubmission,
  fetchImpl: typeof fetch = fetch
): Promise<LocalMediaImportReceipt> {
  const body = new FormData();
  body.set('manifest', submission.manifest);
  body.set('sourceRoot', submission.sourceRoot);
  body.set('storageKey', submission.storageKey);
  if (submission.requestedBy) {
    body.set('requestedBy', submission.requestedBy);
  }

  const response = await fetchImpl(`${BASE}/local-media`, {
    method: 'POST',
    credentials: 'same-origin',
    body
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
