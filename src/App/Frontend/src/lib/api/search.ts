export type SearchScope = 'all' | 'metadata' | 'subtitles' | 'comments';
export type SearchMatch = 'metadata' | 'subtitles' | 'comments' | 'notes' | 'similar';

export interface SearchMediaCard {
  mediaGuid: string;
  title: string;
  thumbnailStoragePath: string | null;
  durationSeconds: number | null;
  releaseDate: string | null;
  viewCount: number | null;
  availability: string | null;
  wasLive: boolean;
  account: {
    accountId: number;
    platform: string;
    accountName: string;
    accountHandle: string;
    avatarStoragePath: string | null;
    userNote: string | null;
  };
  userNote: string | null;
}

export interface SearchHit {
  media: SearchMediaCard;
  matchedIn: SearchMatch[];
}

export interface SearchPage {
  items: SearchHit[];
  page: number;
  totalCount: number;
  hasMore: boolean;
}

export interface SearchOptions {
  scope?: SearchScope;
  page?: number;
  pageSize?: number;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
  signal?: AbortSignal;
}

const BASE = '/api/search';

export async function searchMedia(
  query: string,
  options: SearchOptions = {},
  fetchImpl: typeof fetch = fetch
): Promise<SearchPage> {
  const params = new URLSearchParams({ q: query });
  if (options.scope) params.set('scope', options.scope);
  if (options.page) params.set('page', String(options.page));
  if (options.pageSize) params.set('pageSize', String(options.pageSize));
  if (options.sortBy) params.set('sortBy', options.sortBy);
  if (options.sortOrder) params.set('sortOrder', options.sortOrder);

  const url = `${BASE}?${params}`;
  const response = await fetchImpl(url, { credentials: 'same-origin', signal: options.signal });
  if (!response.ok) {
    throw new Error(await describeError(response, `Search failed with status ${response.status}.`));
  }
  return (await response.json()) as SearchPage;
}

export async function findSimilarMedia(
  mediaGuid: string,
  pageSize = 12,
  fetchImpl: typeof fetch = fetch
): Promise<SearchHit[]> {
  const url = `${BASE}/similar/${encodeURIComponent(mediaGuid)}?pageSize=${pageSize}`;
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `Similar-media lookup failed with status ${response.status}.`));
  }
  return (await response.json()) as SearchHit[];
}

export function searchMatchLabel(match: SearchMatch | string): string {
  switch (match) {
    case 'metadata':
      return 'Title & metadata';
    case 'subtitles':
      return 'Subtitles';
    case 'comments':
      return 'Comments';
    case 'notes':
      return 'Notes';
    case 'similar':
      return 'Similar';
    default:
      return match;
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
