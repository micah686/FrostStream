import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GET } from '../src/routes/auth/login/+server';
import { authCookies } from '$lib/server/auth';
import { FakeCookies, capture, discoveryDocument, jsonResponse, stubFetch } from './helpers';

beforeEach(() => {
  vi.stubEnv('AUTH_AUTHORITY', 'https://authentik.test/application/o/froststream');
  vi.stubEnv('AUTH_CLIENT_ID', 'froststream-bff');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function event(cookies: FakeCookies) {
  return { cookies, url: new URL('https://app.test/auth/login') } as never;
}

describe('GET /auth/login', () => {
  it('sets transient state + verifier cookies and redirects to the authorization endpoint', async () => {
    stubFetch([{ match: '.well-known', respond: () => jsonResponse(discoveryDocument) }]);
    const cookies = new FakeCookies();

    const result = await capture(() => GET(event(cookies)));

    expect(result.status).toBe(303);
    const authorize = new URL(result.location!);
    expect(authorize.origin + authorize.pathname).toBe('https://authentik.test/application/o/authorize/');
    expect(authorize.searchParams.get('client_id')).toBe('froststream-bff');
    expect(authorize.searchParams.get('redirect_uri')).toBe('https://app.test/auth/callback');
    expect(authorize.searchParams.get('response_type')).toBe('code');
    expect(authorize.searchParams.get('scope')).toBe('openid profile email groups');
    expect(authorize.searchParams.get('code_challenge_method')).toBe('S256');

    // The state/challenge sent to the IdP must match what was stashed for the callback to verify.
    const state = cookies.recorded(authCookies.state)!;
    const verifier = cookies.recorded(authCookies.verifier)!;
    expect(state.value).toBe(authorize.searchParams.get('state'));
    expect(authorize.searchParams.get('code_challenge')).toBeTruthy();
    expect(state.options).toMatchObject({ httpOnly: true, secure: true, maxAge: 300 });
    expect(verifier.options).toMatchObject({ httpOnly: true, secure: true, maxAge: 300 });
  });

  it('short-circuits to the app in single-user mode without contacting the IdP', async () => {
    vi.stubEnv('SINGLE_USER_MODE', 'true');
    const fetchMock = stubFetch([]);
    const cookies = new FakeCookies();

    const result = await capture(() => GET(event(cookies)));

    expect(result.status).toBe(303);
    expect(result.location).toBe('/');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(cookies.recorded(authCookies.state)).toBeUndefined();
  });
});
