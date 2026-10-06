import { getJson, sendEmpty, sendJson } from '$lib/api/http';
import type { GranteeType } from '$lib/api/bundles';

export type AccessPolicySyncStatus = 'Pending' | 'Synced' | 'Failed';

export interface AccessPolicyAssignment {
  type: GranteeType;
  id: string;
  displayName?: string | null;
}

export interface AccessPolicy {
  policyId: string;
  name: string;
  description?: string | null;
  enabled: boolean;
  syncStatus: AccessPolicySyncStatus;
  syncError?: string | null;
  version: number;
  bundleIds: string[];
  mediaGuids: string[];
  providers: string[];
  ageThresholds: number[];
  assignments: AccessPolicyAssignment[];
  createdAt: string;
  createdBySubject?: string | null;
  updatedAt: string;
  updatedBySubject?: string | null;
}

export interface AccessPolicyWriteRequest {
  name: string;
  description?: string | null;
  enabled: boolean;
  bundleIds: string[];
  mediaGuids: string[];
  providers: string[];
  ageThresholds: number[];
  assignments: AccessPolicyAssignment[];
}

export interface MediaSummary {
  mediaGuid: string;
  found: boolean;
  title?: string | null;
  providers: string[];
  ageLimit?: number | null;
}

export interface AccessAxisDecision {
  axis: string;
  restricted: boolean;
  allowed: boolean;
  resource?: string | null;
  matchingPolicyIds: string[];
  grantingPolicyIds: string[];
  denyingPolicyIds: string[];
  reason: string;
}

export interface EffectiveMedia {
  mediaGuid: string;
  found: boolean;
  title?: string | null;
  providers: string[];
  ageLimit?: number | null;
  isAllowed: boolean;
  decisions: AccessAxisDecision[];
}

export interface EffectiveAccess {
  principalType: GranteeType;
  principalId: string;
  groups: string[];
  policyIds: string[];
  endpointPolicyIds: string[];
  denyPolicyIds: string[];
  directBundleIds: string[];
  policyBundleIds: string[];
  bundleIds: string[];
  endpointIds: string[];
  endpointId?: string | null;
  endpointAllowed?: boolean | null;
  endpointDecision?: AccessAxisDecision | null;
  deniedMediaGuids: string[];
  deniedProviders: string[];
  deniedAgeThresholds: number[];
  sourcePolicies: EffectiveAccessPolicySource[];
  media?: EffectiveMedia | null;
}

export interface EffectiveAccessPolicySource {
  policyId: string;
  name: string;
  syncStatus: AccessPolicySyncStatus;
  bundleIds: string[];
  deniedMediaGuids: string[];
  deniedProviders: string[];
  deniedAgeThresholds: number[];
  contributesEndpoints: boolean;
  contributesDenies: boolean;
}

export interface EffectiveAccessCheckRequest {
  principalType: GranteeType;
  principalId: string;
  endpointId?: string | null;
  mediaGuid?: string | null;
}

export interface EffectiveAccessCheck {
  principalType: GranteeType;
  principalId: string;
  isAllowed: boolean;
  decisions: AccessAxisDecision[];
  media?: EffectiveMedia | null;
  sourcePolicyIds: string[];
}

const BASE = '/api/global/access-control';

export async function listAccessPolicies(fetchImpl: typeof fetch = fetch): Promise<AccessPolicy[]> {
  return getJson<AccessPolicy[]>(`${BASE}/policies`, fetchImpl);
}

export async function createAccessPolicy(
  request: AccessPolicyWriteRequest,
  fetchImpl: typeof fetch = fetch
): Promise<AccessPolicy> {
  return sendJson<AccessPolicy>(`${BASE}/policies`, 'POST', request, fetchImpl);
}

export async function updateAccessPolicy(
  policyId: string,
  request: AccessPolicyWriteRequest,
  fetchImpl: typeof fetch = fetch
): Promise<AccessPolicy> {
  return sendJson<AccessPolicy>(`${BASE}/policies/${encodeURIComponent(policyId)}`, 'PUT', request, fetchImpl);
}

export async function duplicateAccessPolicy(
  policyId: string,
  name: string,
  fetchImpl: typeof fetch = fetch
): Promise<AccessPolicy> {
  return sendJson<AccessPolicy>(
    `${BASE}/policies/${encodeURIComponent(policyId)}/duplicate`,
    'POST',
    { name },
    fetchImpl
  );
}

export async function deleteAccessPolicy(policyId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  await sendEmpty(`${BASE}/policies/${encodeURIComponent(policyId)}`, 'DELETE', fetchImpl);
}

export async function listPolicyProviders(fetchImpl: typeof fetch = fetch): Promise<string[]> {
  return getJson<string[]>(`${BASE}/providers`, fetchImpl);
}

export async function getPolicyMediaSummary(mediaGuid: string, fetchImpl: typeof fetch = fetch): Promise<MediaSummary> {
  return getJson<MediaSummary>(`${BASE}/media/${encodeURIComponent(mediaGuid)}`, fetchImpl);
}

export async function getEffectiveAccess(
  principalType: GranteeType,
  principalId: string,
  endpointId?: string,
  mediaGuid?: string,
  fetchImpl: typeof fetch = fetch
): Promise<EffectiveAccess> {
  const query = new URLSearchParams({ principalType, principalId });
  if (endpointId?.trim()) query.set('endpointId', endpointId.trim());
  if (mediaGuid?.trim()) query.set('mediaGuid', mediaGuid.trim());
  return getJson<EffectiveAccess>(`${BASE}/effective?${query}`, fetchImpl);
}

export async function checkEffectiveAccess(
  request: EffectiveAccessCheckRequest,
  fetchImpl: typeof fetch = fetch
): Promise<EffectiveAccessCheck> {
  return sendJson<EffectiveAccessCheck>(`${BASE}/effective/check`, 'POST', request, fetchImpl);
}

