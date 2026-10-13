import type { MediaCard } from './watchState';

export interface MediaLikeState {
  ownerSubject: string;
  mediaGuid: string;
  liked: boolean;
  likedAt: string | null;
  updatedAt: string | null;
}

export interface LikedMediaItem {
  like: MediaLikeState;
  media: MediaCard;
}

export interface LikedMediaResponse {
  items: LikedMediaItem[];
  page: number;
  totalCount: number;
  hasMore: boolean;
}

const likeUrl = (mediaGuid: string) => `/api/media/${encodeURIComponent(mediaGuid)}/like`;

export async function getLikeState(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<MediaLikeState> {
  const url = `/api/media/${encodeURIComponent(mediaGuid)}/like-state`;
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as MediaLikeState;
}

export async function likeMedia(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<MediaLikeState> {
  const url = likeUrl(mediaGuid);
  const response = await fetchImpl(url, { method: 'POST', credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `POST ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as MediaLikeState;
}

export async function unlikeMedia(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<MediaLikeState> {
  const url = likeUrl(mediaGuid);
  const response = await fetchImpl(url, { method: 'DELETE', credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as MediaLikeState;
}

export async function listLikedMedia(
  page = 1,
  pageSize = 24,
  fetchImpl: typeof fetch = fetch
): Promise<LikedMediaResponse> {
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  const url = `/api/media/likes?${query}`;
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as LikedMediaResponse;
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
