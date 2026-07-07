import { getJson } from '$lib/api/http';

const BASE = '/api/statistics';

export type StatisticsBucket = 'day' | 'week' | 'month';

export interface InventoryStatistics {
  totalMedia: number;
  totalChannels: number;
  totalCreatorSources: number;
  totalPlaylists: number;
  totalDownloads: number;
  totalBytes: number;
  totalDurationSeconds: number;
}

export interface WatchStatistics {
  watchedCount: number;
  watchedPercent: number;
  unwatchedCount: number;
  unwatchedPercent: number;
  watchProgressSeconds: number;
  watchProgressPercent: number;
}

export interface MediaTypeStatistics {
  type: string;
  count: number;
  durationSeconds: number;
  bytes: number;
}

export interface DownloadStateStatistics {
  state: string;
  count: number;
}

export interface StatisticsOverview {
  inventory: InventoryStatistics;
  watchProgress: WatchStatistics;
  mediaTypes: MediaTypeStatistics[];
  downloadStates: DownloadStateStatistics[];
}

export interface ChannelStatisticsSummary {
  creatorSourceId: number | null;
  platform: string;
  sourceType: string | null;
  sourceUrl: string | null;
  accountId: number | null;
  accountName: string | null;
  accountHandle: string | null;
  avatarStoragePath: string | null;
  availableCount: number;
  downloadedCount: number;
  downloadedPercent: number;
  totalDurationSeconds: number;
  downloadedDurationSeconds: number;
  totalBytes: number;
  lastSuccessfulScanAt: string | null;
  lastFullScanAt: string | null;
}

export interface ChannelStatisticsDetail {
  summary: ChannelStatisticsSummary;
  ignoredCount: number;
  unavailableCount: number;
  removedCount: number;
  mediaTypes: MediaTypeStatistics[];
  recentDownloadStates: DownloadStateStatistics[];
}

export interface ChannelStatisticsListResponse {
  items: ChannelStatisticsSummary[];
  page: number;
  totalCount: number;
  hasMore: boolean;
}

export interface DownloadHistoryBucket {
  bucketStart: string;
  bucketEnd: string;
  created: number;
  completed: number;
  failed: number;
  cancelled: number;
  ignored: number;
  bytesCompleted: number;
  durationCompletedSeconds: number;
}

export interface ListChannelStatisticsOptions {
  pageSize?: number;
  page?: number;
  sortBy?: 'downloaded' | 'available' | 'duration' | 'bytes' | 'name';
  sortOrder?: 'asc' | 'desc';
}

export interface DownloadStatisticsOptions {
  from: string | Date;
  to: string | Date;
  bucket?: StatisticsBucket;
}

export async function getGlobalStatistics(fetchImpl: typeof fetch = fetch): Promise<StatisticsOverview> {
  return getJson<StatisticsOverview>(`${BASE}/overview`, fetchImpl);
}

export async function listChannelStatistics(
  options: ListChannelStatisticsOptions = {},
  fetchImpl: typeof fetch = fetch
): Promise<ChannelStatisticsListResponse> {
  const query = new URLSearchParams();
  if (options.pageSize) query.set('pageSize', String(options.pageSize));
  if (options.page) query.set('page', String(options.page));
  if (options.sortBy) query.set('sortBy', options.sortBy);
  if (options.sortOrder) query.set('sortOrder', options.sortOrder);
  const suffix = query.size > 0 ? `?${query}` : '';
  return getJson<ChannelStatisticsListResponse>(`${BASE}/channels${suffix}`, fetchImpl);
}

export async function getChannelStatistics(
  creatorSourceId: number,
  fetchImpl: typeof fetch = fetch
): Promise<ChannelStatisticsDetail> {
  return getJson<ChannelStatisticsDetail>(`${BASE}/channels/${encodeURIComponent(creatorSourceId)}`, fetchImpl);
}

export async function getDownloadStatistics(
  options: DownloadStatisticsOptions,
  fetchImpl: typeof fetch = fetch
): Promise<DownloadHistoryBucket[]> {
  const query = new URLSearchParams({
    from: serializeDate(options.from),
    to: serializeDate(options.to),
    bucket: options.bucket ?? 'day'
  });
  return getJson<DownloadHistoryBucket[]>(`${BASE}/download-history?${query}`, fetchImpl);
}

function serializeDate(value: string | Date): string {
  return value instanceof Date ? value.toISOString() : value;
}
