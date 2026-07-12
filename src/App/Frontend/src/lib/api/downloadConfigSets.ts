export type AudioRenditionFormat = 'Aac' | 'Opus' | 'Mp3';
export type IgnoreKeywordMatchType = 'Substring' | 'Regex';

export interface IgnoreKeyword {
  pattern: string;
  matchType: IgnoreKeywordMatchType;
}

export interface DownloadConfigSet {
  id: number;
  key: string;
  name: string;
  description: string | null;
  storageKey: string | null;
  cookieProfileKey: string | null;
  ytDlpOptions: Record<string, unknown> | null;
  ignoreKeywords: IgnoreKeyword[];
  encodeForPlaylist: boolean;
  audioFormat: AudioRenditionFormat;
  priority: number;
  fetchComments: boolean;
}

export interface DownloadConfigSetRequest {
  key: string;
  name: string;
  description: string | null;
  storageKey: string | null;
  cookieProfileKey: string | null;
  ytDlpOptions: Record<string, unknown> | null;
  ignoreKeywords: IgnoreKeyword[];
  encodeForPlaylist: boolean;
  audioFormat: AudioRenditionFormat;
  priority: number;
  fetchComments: boolean;
}

const BASE = '/api/user/config-sets';

export async function listDownloadConfigSets(fetchImpl: typeof fetch = fetch): Promise<DownloadConfigSet[]> {
  return getJson<DownloadConfigSet[]>(BASE, fetchImpl);
}

export async function getDownloadConfigSet(key: string, fetchImpl: typeof fetch = fetch): Promise<DownloadConfigSet> {
  return getJson<DownloadConfigSet>(`${BASE}/${encodeURIComponent(key)}`, fetchImpl);
}

export async function createDownloadConfigSet(
  request: DownloadConfigSetRequest,
  fetchImpl: typeof fetch = fetch
): Promise<DownloadConfigSet> {
  return sendJson<DownloadConfigSet>(BASE, 'POST', request, fetchImpl);
}

export async function updateDownloadConfigSet(
  key: string,
  request: DownloadConfigSetRequest,
  fetchImpl: typeof fetch = fetch
): Promise<DownloadConfigSet> {
  return sendJson<DownloadConfigSet>(`${BASE}/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function deleteDownloadConfigSet(key: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl(`${BASE}/${encodeURIComponent(key)}`, {
    method: 'DELETE',
    credentials: 'same-origin'
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${BASE}/${key} failed with status ${response.status}.`));
  }
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
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
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
