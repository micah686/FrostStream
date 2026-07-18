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
  items: ChannelAudioItem[];
}

export interface PodcastFeedLink {
  feedUrl: string;
  expiresAt: string;
}

const base = (accountId: number) => `/api/media/channels/${encodeURIComponent(accountId)}/audio`;

export function getChannelAudioStatus(accountId: number, fetchImpl: typeof fetch = fetch): Promise<ChannelAudioStatus> {
  return getJson<ChannelAudioStatus>(`${base(accountId)}/status`, fetchImpl);
}

export function encodeChannelAudio(accountId: number, fetchImpl: typeof fetch = fetch): Promise<ChannelAudioStatus> {
  return sendJson<ChannelAudioStatus>(`${base(accountId)}/encode`, 'POST', undefined, fetchImpl);
}

export function createPodcastFeedLink(accountId: number, fetchImpl: typeof fetch = fetch): Promise<PodcastFeedLink> {
  return sendJson<PodcastFeedLink>(`${base(accountId)}/podcast-token`, 'POST', undefined, fetchImpl);
}
