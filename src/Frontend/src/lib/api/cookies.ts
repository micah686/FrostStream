export interface CookieProfile {
  profileKey: string;
  site: string | null;
  displayName: string | null;
  createdAt: string | null;
  lastUpdated: string | null;
}

export interface CookieUpsertRequest {
  /** Netscape-formatted cookie text. Write-only: never returned by the API. */
  content: string;
  site?: string | null;
  displayName?: string | null;
}

export const COOKIE_PROFILE_KEY_PATTERN = /^[a-z0-9-]{2,100}$/;

const BASE = '/api/user/cookies';

export async function listCookieProfiles(fetchImpl: typeof fetch = fetch): Promise<CookieProfile[]> {
  return getJson<CookieProfile[]>(BASE, fetchImpl);
}

export async function getCookieProfile(profileKey: string, fetchImpl: typeof fetch = fetch): Promise<CookieProfile> {
  return getJson<CookieProfile>(`${BASE}/${encodeURIComponent(profileKey)}`, fetchImpl);
}

export async function upsertCookieProfile(
  profileKey: string,
  request: CookieUpsertRequest,
  fetchImpl: typeof fetch = fetch
): Promise<CookieProfile> {
  const url = `${BASE}/${encodeURIComponent(profileKey)}`;
  const response = await fetchImpl(url, {
    method: 'PUT',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(request)
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `PUT ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as CookieProfile;
}

export async function deleteCookieProfile(profileKey: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const url = `${BASE}/${encodeURIComponent(profileKey)}`;
  const response = await fetchImpl(url, {
    method: 'DELETE',
    credentials: 'same-origin'
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${url} failed with status ${response.status}.`));
  }
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
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
