import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GET } from '../src/routes/auth/logout/+server';
import { authCookies, writeTokens } from '$lib/server/auth';
import { FakeCookies, capture, discoveryDocument, jsonResponse, stubFetch } from './helpers';

beforeEach(() => {
  vi.stubEnv('AUTH_AUTHORITY', 'https://authentik.test/application/o/froststream');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function event(cookies: FakeCookies) {
  return { cookies, url: new URL('https://app.test/auth/logout') } as never;
}

describe('GET /auth/logout', () => {
  it('clears auth cookies and redirects to the IdP end-session URL when advertised', async () => {
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'a', idToken: 'id-token', expiresAt: Date.now() + 3600_000 }, true);
    stubFetch([{ match: '.well-known', respond: () => jsonResponse(discoveryDocument) }]);

    const result = await capture(() => GET(event(cookies)));

    expect(result.status).toBe(303);
    const target = new URL(result.location!);
    expect(target.origin + target.pathname).toBe('https://authentik.test/application/o/froststream/end-session/');
    expect(target.searchParams.get('id_token_hint')).toBe('id-token');
    expect(target.searchParams.get('post_logout_redirect_uri')).toBe('https://app.test/');

    // Local session is cleared regardless.
    for (const name of [authCookies.token, authCookies.state, authCookies.verifier]) {
      expect(cookies.deleted.has(name)).toBe(true);
    }
  });

  it('falls back to a local redirect when the provider has no end-session endpoint', async () => {
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'a', idToken: 'id', expiresAt: Date.now() + 3600_000 }, false);
    const { end_session_endpoint, ...withoutEndSession } = discoveryDocument;
    void end_session_endpoint;
    stubFetch([{ match: '.well-known', respond: () => jsonResponse(withoutEndSession) }]);

    const result = await capture(() => GET(event(cookies)));

    expect(result.status).toBe(303);
    expect(result.location).toBe('/');
    expect(cookies.deleted.has(authCookies.token)).toBe(true);
  });

  it('does only a local logout in single-user mode', async () => {
    vi.stubEnv('SINGLE_USER_MODE', 'true');
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'a', expiresAt: Date.now() + 3600_000 }, false);
    const fetchMock = stubFetch([]);

    const result = await capture(() => GET(event(cookies)));

    expect(result.status).toBe(303);
    expect(result.location).toBe('/');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(cookies.deleted.has(authCookies.token)).toBe(true);
  });
});
