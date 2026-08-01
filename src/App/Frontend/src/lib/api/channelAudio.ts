import { getJson, sendJson } from '$lib/api/http';

export type AudioRenditionStatus = 'Pending' | 'Processing' | 'Ready' | 'Failed';

export interface ChannelAudioRendition {
  renditionId: string;
  mediaGuid: string;
  sourceVersion: number;
  status: AudioRenditionStatus;
  storageKey: string;
  storagePath: string | null;
  sizeBytes: number | null;
  durationSeconds: number | null;
  errorMessage: string | null;
}

export interface ChannelAudioItem {
  mediaGuid: string;
  title: string;
  description: string | null;
  releaseDate: string | null;
  durationSeconds: number | null;
  rendition: ChannelAudioRendition | null;
}

export interface ChannelAudioStatus {
  accountId: number;
  accountName: string;
  accountDescription: string | null;
  avatarStoragePath: string | null;
  totalCount: number;
  missingCount: number;
  pendingCount: number;
  processingCount: number;
  readyCount: number;
  failedCount: number;
  availableStorageKeys: string[];
  items: ChannelAudioItem[];
}

export interface PodcastFeedLink {
  feedUrl: string;
  expiresAt: string;
}

// Durable, job-independent per-media encoded flag (media.audio_encoding_status), separate from the
// rendition/job queue counts in ChannelAudioStatus. Use this when checking how much of a channel is
// encoded — it's a cheap indexed count instead of a full load-and-diff of every item's job state.
export interface ChannelAudioEncodedItem {
  mediaGuid: string;
  title: string;
  isEncoded: boolean;
  storageKey: string | null;
  storagePath: string | null;
  encodedAt: string | null;
}

export interface ChannelAudioEncodedStatusResponse {
  items: ChannelAudioEncodedItem[];
  nextCursor: string | null;
  totalCount: number;
  encodedCount: number;
}

export interface ChannelAudioEncodedStatusParams {
  isEncoded?: boolean;
  storageKey?: string;
  limit?: number;
  cursor?: string;
}

// One live MediaProcessor progress frame from the rendition SSE stream (advisory, live-only).
export interface RenditionProgressFrame {
  renditionId: string;
  kind: 'Stream' | 'Audio';
  mediaGuid: string;
  sequence: number;
  occurredAt: string;
  phase: 'FetchingSource' | 'Probing' | 'Encoding' | 'Packaging' | 'Uploading' | 'Ready' | 'Failed' | string;
  percent: number | null;
  speedX: number | null;
  etaSeconds: number | null;
  message: string | null;
}

export const renditionProgressStreamUrl = (): string => '/api/media/renditions/progress/stream';

const base = (accountId: number) => `/api/media/channels/${encodeURIComponent(accountId)}/audio`;

const withStorageKey = (path: string, storageKey?: string) =>
  storageKey ? `${path}?storageKey=${encodeURIComponent(storageKey)}` : path;

export function getChannelAudioStatus(
  accountId: number,
  storageKey?: string,
  fetchImpl: typeof fetch = fetch
): Promise<ChannelAudioStatus> {
  return getJson<ChannelAudioStatus>(withStorageKey(`${base(accountId)}/status`, storageKey), fetchImpl);
}

export function encodeChannelAudio(
  accountId: number,
  storageKey?: string,
  fetchImpl: typeof fetch = fetch
): Promise<ChannelAudioStatus> {
  return sendJson<ChannelAudioStatus>(withStorageKey(`${base(accountId)}/encode`, storageKey), 'POST', undefined, fetchImpl);
}

export function createPodcastFeedLink(accountId: number, fetchImpl: typeof fetch = fetch): Promise<PodcastFeedLink> {
  return sendJson<PodcastFeedLink>(`${base(accountId)}/podcast-token`, 'POST', undefined, fetchImpl);
}

export function getChannelAudioEncodedStatus(
  accountId: number,
  params: ChannelAudioEncodedStatusParams = {},
  fetchImpl: typeof fetch = fetch
): Promise<ChannelAudioEncodedStatusResponse> {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      query.set(key, String(value));
    }
  }

  const suffix = query.toString();
  return getJson<ChannelAudioEncodedStatusResponse>(
    `${base(accountId)}/encoded-status${suffix ? `?${suffix}` : ''}`,
    fetchImpl
  );
}
