import { getJson } from '$lib/api/http';

export type RenditionKind = 'Stream' | 'Audio';
export type RenditionStatus = 'Pending' | 'Processing' | 'Ready' | 'Failed';

export interface RenditionQueueItem {
  renditionId: string;
  kind: RenditionKind;
  mediaGuid: string;
  title: string;
  sourceVersion: number;
  status: RenditionStatus;
  storageKey: string;
  storagePath: string | null;
  sizeBytes: number | null;
  durationSeconds: number | null;
  errorMessage: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface RenditionQueueListResponse {
  items: RenditionQueueItem[];
  nextCursor: string | null;
  totalCount: number;
}

export interface RenditionQueueListParams {
  kind?: RenditionKind;
  status?: RenditionStatus;
  storageKey?: string;
  q?: string;
  limit?: number;
  cursor?: string;
}

const BASE = '/api/media/renditions/queue';

/** Live progress SSE stream — shared with the channel page's per-item overlay; unfiltered here for every rendition. */
export const renditionQueueStreamUrl = (): string => '/api/media/renditions/progress/stream';

export async function fetchRenditionQueue(
  params: RenditionQueueListParams = {},
  fetchImpl: typeof fetch = fetch
): Promise<RenditionQueueListResponse> {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      query.set(key, String(value));
    }
  }

  const suffix = query.toString();
  return getJson<RenditionQueueListResponse>(`${BASE}${suffix ? `?${suffix}` : ''}`, fetchImpl);
}
