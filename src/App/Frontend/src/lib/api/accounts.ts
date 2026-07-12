import { getJson } from '$lib/api/http';

const BASE = '/api/metadata/accounts';

export interface AccountSummary {
  accountId: number;
  platform: string;
  accountName: string;
  accountHandle: string;
  accountUrl?: string | null;
  followerCount?: number | null;
  isVerified: boolean;
  avatarStoragePath?: string | null;
  mediaCount: number;
  userNote?: string | null;
}

export interface AccountDetail extends AccountSummary {
  accountCreationDate?: string | null;
  description?: string | null;
  bannerStoragePath?: string | null;
}

export interface AccountListResponse {
  items: AccountSummary[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface ListAccountsOptions {
  pageSize?: number;
  after?: string | null;
  platform?: string | null;
}

export async function listAccounts(
  options: ListAccountsOptions = {},
  fetchImpl: typeof fetch = fetch
): Promise<AccountListResponse> {
  const query = new URLSearchParams();
  if (options.pageSize) query.set('pageSize', String(options.pageSize));
  if (options.after) query.set('after', options.after);
  if (options.platform?.trim()) query.set('platform', options.platform.trim());
  const suffix = query.size > 0 ? `?${query}` : '';
  return getJson<AccountListResponse>(`${BASE}${suffix}`, fetchImpl);
}

export async function getAccount(accountId: number, fetchImpl: typeof fetch = fetch): Promise<AccountDetail> {
  return getJson<AccountDetail>(`${BASE}/${encodeURIComponent(accountId)}`, fetchImpl);
}
