import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GET } from '../src/routes/auth/callback/+server';
import { authCookies, readTokens } from '$lib/server/auth';
import { FakeCookies, capture, discoveryDocument, jsonResponse, stubFetch } from './helpers';

beforeEach(() => {
  vi.stubEnv('AUTH_AUTHORITY', 'https://authentik.test/application/o/froststream');
  vi.stubEnv('AUTH_CLIENT_ID', 'froststream-bff');
  vi.stubEnv('API_BASE_URL', 'https://api.test');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function event(cookies: FakeCookies, query: Record<string, string>) {
  const url = new URL('https://app.test/auth/callback');
  for (const [k, v] of Object.entries(query)) {
    url.searchParams.set(k, v);
  }
  return { cookies, url } as never;
}

describe('GET /auth/callback', () => {
  it('rejects a callback whose state does not match the stored one', async () => {
    const cookies = new FakeCookies({ [authCookies.state]: 'expected', [authCookies.verifier]: 'v' });
    const fetchMock = stubFetch([]);

    const result = await capture(() => GET(event(cookies, { state: 'tampered', code: 'c' })));

    expect(result.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('rejects a callback that is missing the verifier cookie', async () => {
    const cookies = new FakeCookies({ [authCookies.state]: 's' });
    const result = await capture(() => GET(event(cookies, { state: 's', code: 'c' })));
    expect(result.status).toBe(400);
  });

  it('exchanges the code, stores tokens httpOnly, clears transient cookies and syncs the session', async () => {
    const cookies = new FakeCookies({ [authCookies.state]: 's', [authCookies.verifier]: 'v' });
    let sessionAuthHeader: string | undefined;

    const fetchMock = stubFetch([
      { match: '.well-known', respond: () => jsonResponse(discoveryDocument) },
      {
        match: '/token/',
        respond: () => jsonResponse({ access_token: 'at', refresh_token: 'rt', id_token: 'it', expires_in: 3600 })
      },
      {
        match: '/api/auth/session',
        respond: (_url, init) => {
          sessionAuthHeader = (init?.headers as Record<string, string>).authorization;
          return jsonResponse({});
        }
      }
    ]);

    const result = await capture(() => GET(event(cookies, { state: 's', code: 'the-code' })));

    expect(result.status).toBe(303);
    expect(result.location).toBe('/');

    // Tokens persisted to the httpOnly cookie.
    const tokenCookie = cookies.recorded(authCookies.token)!;
    expect(tokenCookie.options.httpOnly).toBe(true);
    expect(readTokens(cookies as never)?.accessToken).toBe('at');

    // Transient OIDC cookies are removed.
    expect(cookies.deleted.has(authCookies.state)).toBe(true);
    expect(cookies.deleted.has(authCookies.verifier)).toBe(true);

    // Session sync ran with the freshly minted access token.
    expect(sessionAuthHeader).toBe('Bearer at');
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });
});
