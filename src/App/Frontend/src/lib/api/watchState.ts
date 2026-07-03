export interface WatchState {
  ownerSubject: string;
  mediaGuid: string;
  positionSeconds: number | null;
  durationSeconds: number | null;
  completed: boolean;
  watchedAt: string | null;
  lastPlayedAt: string;
  updatedAt: string;
}

export interface WatchStateUpdate {
  positionSeconds?: number | null;
  durationSeconds?: number | null;
  completed: boolean;
}

const base = (mediaGuid: string) => `/api/media/${encodeURIComponent(mediaGuid)}/watch-state`;

/** Returns null when the caller has no recorded state for the media item (404). */
export async function getWatchState(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<WatchState | null> {
  const url = base(mediaGuid);
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as WatchState;
}

export async function updateWatchState(
  mediaGuid: string,
  update: WatchStateUpdate,
  fetchImpl: typeof fetch = fetch
): Promise<WatchState> {
  const url = base(mediaGuid);
  const response = await fetchImpl(url, {
    method: 'PUT',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(update),
    // Progress updates fire on navigation/unload too; keepalive lets the final one land.
    keepalive: true
  });
  if (!response.ok) {
    throw new Error(await describeError(response, `PUT ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as WatchState;
}

export async function markWatched(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<WatchState> {
  return postState(`${base(mediaGuid)}/watched`, fetchImpl);
}

export async function markUnwatched(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<WatchState> {
  return postState(`${base(mediaGuid)}/unwatched`, fetchImpl);
}

export async function listInProgress(limit = 12, fetchImpl: typeof fetch = fetch): Promise<WatchState[]> {
  const url = `/api/media/watch-states/in-progress?limit=${limit}`;
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as WatchState[];
}

async function postState(url: string, fetchImpl: typeof fetch): Promise<WatchState> {
  const response = await fetchImpl(url, { method: 'POST', credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `POST ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as WatchState;
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
