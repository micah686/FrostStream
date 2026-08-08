export interface MediaDeleteResult {
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  filesDeleted: number;
  mediaRemoved: boolean;
}

export interface MetadataVersion {
  mediaGuid: string;
  versionNum: number;
  storageKey: string;
  storagePath: string;
  contentHashXxh128: string;
  ingestOrigin: string;
}

export interface MetadataVersionsResponse {
  totalCount: number;
  versions: MetadataVersion[];
}

const BASE = '/api/global/metadata';

export async function triggerReindex(fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/reindex`, 'POST', fetchImpl);
}

export async function triggerDatabaseReindex(fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/database-reindex`, 'POST', fetchImpl);
}

export async function deleteMedia(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<MediaDeleteResult> {
  return sendJson<MediaDeleteResult>(`${BASE}/${encodeURIComponent(mediaGuid)}`, 'DELETE', undefined, fetchImpl);
}

export async function deleteMediaForStorageKey(
  mediaGuid: string,
  storageKey: string,
  fetchImpl: typeof fetch = fetch
): Promise<MediaDeleteResult> {
  return sendJson<MediaDeleteResult>(
    `${BASE}/${encodeURIComponent(mediaGuid)}/storage/${encodeURIComponent(storageKey)}`,
    'DELETE',
    undefined,
    fetchImpl
  );
}

export async function refreshAccountAssets(
  accountId: number,
  force = false,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(`/api/metadata/accounts/${accountId}/refresh-assets?force=${force}`, 'POST', fetchImpl);
}

export async function generateMissingAccountThumbnails(
  accountId: number,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(`/api/metadata/accounts/${accountId}/generate-missing-thumbnails`, 'POST', fetchImpl);
}

export async function getMetadataVersions(
  mediaGuid: string,
  fetchImpl: typeof fetch = fetch
): Promise<MetadataVersionsResponse> {
  return getJson<MetadataVersionsResponse>(`/api/metadata/${encodeURIComponent(mediaGuid)}/versions`, fetchImpl);
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendJson<T>(url: string, method: string, body: unknown, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, {
    method,
    credentials: 'same-origin',
    ...(body === undefined
      ? {}
      : { headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendEmpty(url: string, method: string, fetchImpl: typeof fetch): Promise<void> {
  const response = await fetchImpl(url, { method, credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
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
