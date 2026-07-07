export interface UserPlaylistItem {
  mediaGuid: string;
  position: number;
  addedAt: string;
}

export interface UserPlaylist {
  playlistId: string;
  name: string;
  description: string | null;
  createdAt: string;
  updatedAt: string;
  itemCount: number;
  userNote: string | null;
  /** Only populated on the single-playlist endpoints; list responses omit item details. */
  items: UserPlaylistItem[] | null;
}

export interface UserPlaylistSaveRequest {
  name: string;
  description?: string | null;
}

const BASE = '/api/user/playlists';

export async function listUserPlaylists(
  pageSize = 50,
  pageOffset = 0,
  fetchImpl: typeof fetch = fetch
): Promise<UserPlaylist[]> {
  return getJson<UserPlaylist[]>(`${BASE}?pageSize=${pageSize}&pageOffset=${pageOffset}`, fetchImpl);
}

export async function getUserPlaylist(playlistId: string, fetchImpl: typeof fetch = fetch): Promise<UserPlaylist> {
  return getJson<UserPlaylist>(`${BASE}/${encodeURIComponent(playlistId)}`, fetchImpl);
}

export async function createUserPlaylist(
  request: UserPlaylistSaveRequest,
  fetchImpl: typeof fetch = fetch
): Promise<UserPlaylist> {
  return sendJson<UserPlaylist>(BASE, 'POST', request, fetchImpl);
}

export async function updateUserPlaylist(
  playlistId: string,
  request: UserPlaylistSaveRequest,
  fetchImpl: typeof fetch = fetch
): Promise<UserPlaylist> {
  return sendJson<UserPlaylist>(`${BASE}/${encodeURIComponent(playlistId)}`, 'PATCH', request, fetchImpl);
}

export async function deleteUserPlaylist(playlistId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const url = `${BASE}/${encodeURIComponent(playlistId)}`;
  const response = await fetchImpl(url, { method: 'DELETE', credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${url} failed with status ${response.status}.`));
  }
}

export async function addUserPlaylistItem(
  playlistId: string,
  mediaGuid: string,
  position?: number,
  fetchImpl: typeof fetch = fetch
): Promise<UserPlaylist> {
  return sendJson<UserPlaylist>(
    `${BASE}/${encodeURIComponent(playlistId)}/items`,
    'POST',
    { mediaGuid, position },
    fetchImpl
  );
}

export async function removeUserPlaylistItem(
  playlistId: string,
  mediaGuid: string,
  fetchImpl: typeof fetch = fetch
): Promise<UserPlaylist> {
  const url = `${BASE}/${encodeURIComponent(playlistId)}/items/${encodeURIComponent(mediaGuid)}`;
  const response = await fetchImpl(url, { method: 'DELETE', credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as UserPlaylist;
}

export async function reorderUserPlaylistItems(
  playlistId: string,
  mediaGuids: string[],
  fetchImpl: typeof fetch = fetch
): Promise<UserPlaylist> {
  return sendJson<UserPlaylist>(
    `${BASE}/${encodeURIComponent(playlistId)}/items/order`,
    'PUT',
    { mediaGuids },
    fetchImpl
  );
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
