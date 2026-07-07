import { getJson, sendEmpty, sendJson } from '$lib/api/http';

export type CreatorSourceType = 'Videos' | 'Shorts' | 'Streams' | 'Playlist' | 'Clips' | 'Vods';

export const creatorSourceTypes: CreatorSourceType[] = [
  'Videos',
  'Shorts',
  'Streams',
  'Playlist',
  'Clips',
  'Vods'
];

export interface CreatorSource {
  id: number;
  platform: string;
  sourceType: CreatorSourceType;
  sourceUrl: string;
  scanEnabled: boolean;
  incrementalPageSize: number;
  consecutiveKnownThreshold: number;
  fullRescanIntervalDays: number;
  metadataRefreshWindow: number;
  providerQueryLimits: Record<string, unknown> | null;
  lastSuccessfulScanAt: string | null;
  lastFullScanAt: string | null;
  lastSeenHighWatermark: string | null;
  nextFullScanStartIndex: number | null;
  createdAt: string;
  lastUpdated: string | null;
}

export interface CreatorSourceRequest {
  platform: string;
  sourceType: CreatorSourceType;
  sourceUrl: string;
  scanEnabled: boolean;
  incrementalPageSize: number;
  consecutiveKnownThreshold: number;
  fullRescanIntervalDays: number;
  metadataRefreshWindow: number;
  providerQueryLimits?: Record<string, unknown> | null;
}

export interface ChannelDownloadRequest {
  sourceUrl: string;
  platform?: string;
  sourceType?: CreatorSourceType;
  storageKey?: string | null;
  configSetKey?: string | null;
  cookieProfileKey?: string | null;
  encodeForPlaylist?: boolean | null;
  priority?: number | null;
  fetchComments?: boolean | null;
}

export interface ChannelDownloadResponse {
  sourceId: number;
  sourceUrl: string;
  platform: string;
  sourceType: CreatorSourceType;
  queued: boolean;
  idempotencyKey: string;
}

export interface IgnoredMedia {
  id: number;
  creatorSourceId: number;
  title: string | null;
  canonicalUrl: string;
  ignoredKeyword: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
}

const BASE = '/api/creator-monitor';

export async function listCreatorSources(fetchImpl: typeof fetch = fetch): Promise<CreatorSource[]> {
  return getJson<CreatorSource[]>(BASE, fetchImpl);
}

export async function getCreatorSource(id: number, fetchImpl: typeof fetch = fetch): Promise<CreatorSource> {
  return getJson<CreatorSource>(`${BASE}/${id}`, fetchImpl);
}

export async function createCreatorSource(
  request: CreatorSourceRequest,
  fetchImpl: typeof fetch = fetch
): Promise<CreatorSource> {
  return sendJson<CreatorSource>(BASE, 'POST', request, fetchImpl);
}

export async function updateCreatorSource(
  id: number,
  request: CreatorSourceRequest,
  fetchImpl: typeof fetch = fetch
): Promise<CreatorSource> {
  return sendJson<CreatorSource>(`${BASE}/${id}`, 'PUT', request, fetchImpl);
}

export async function deleteCreatorSource(id: number, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/${id}`, 'DELETE', fetchImpl);
}

export async function refreshCreatorAssets(
  id: number,
  force = false,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendJson<unknown>(`${BASE}/${id}/refresh-assets?force=${force}`, 'POST', undefined, fetchImpl);
}

export async function listIgnoredMedia(id: number, fetchImpl: typeof fetch = fetch): Promise<IgnoredMedia[]> {
  return getJson<IgnoredMedia[]>(`${BASE}/${id}/ignored-media`, fetchImpl);
}

export async function queueChannelDownload(
  request: ChannelDownloadRequest,
  fetchImpl: typeof fetch = fetch
): Promise<ChannelDownloadResponse> {
  return sendJson<ChannelDownloadResponse>(`${BASE}/channel-downloads`, 'POST', request, fetchImpl);
}
