// Platform playlists: playlists ingested from a provider (e.g. a downloaded YouTube
// playlist) whose item order comes from the provider. Distinct from user playlists
// (see userPlaylists.ts), which are private per-user collections.

export interface PlatformPlaylistItem {
  playlistIndex: number;
  jobId: string;
  entryUrl: string;
  entryTitle: string | null;
  jobState: string;
  mediaGuid: string | null;
  ignoredKeyword: string | null;
}

export interface PlatformPlaylist {
  playlistId: string;
  state: string;
  sourceUrl: string;
  providerPlaylistId: string | null;
  title: string | null;
  totalItems: number;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  lastScannedAt: string | null;
  completedItems: number;
  failedItems: number;
  pendingItems: number;
  userNote: string | null;
  /** Populated only by the get-by-id endpoint; null on list responses. */
  items: PlatformPlaylistItem[] | null;
}

export interface PlaylistDownloadRequest {
  sourceUrl: string;
  storageKey?: string | null;
  configSetKey?: string | null;
  cookieProfileKey?: string | null;
  encodeForPlaylist?: boolean | null;
  priority?: number | null;
  fetchComments?: boolean | null;
}

export interface PlaylistDownloadResponse {
  playlistId: string;
  correlationId: string;
}

const BASE = '/api/playlists';

export async function queuePlaylistDownload(
  request: PlaylistDownloadRequest,
  fetchImpl: typeof fetch = fetch
): Promise<PlaylistDownloadResponse> {
  const response = await fetchImpl(BASE, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(request)
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `POST ${BASE} failed with status ${response.status}.`));
  }
  return (await response.json()) as PlaylistDownloadResponse;
}

export async function listPlatformPlaylists(
  pageSize = 50,
  pageOffset = 0,
  fetchImpl: typeof fetch = fetch
): Promise<PlatformPlaylist[]> {
  return getJson<PlatformPlaylist[]>(`${BASE}?pageSize=${pageSize}&pageOffset=${pageOffset}`, fetchImpl);
}

export async function getPlatformPlaylist(
  playlistId: string,
  fetchImpl: typeof fetch = fetch
): Promise<PlatformPlaylist> {
  return getJson<PlatformPlaylist>(`${BASE}/${encodeURIComponent(playlistId)}`, fetchImpl);
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
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
