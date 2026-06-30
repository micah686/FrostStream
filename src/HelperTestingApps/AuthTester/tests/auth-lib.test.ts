import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  authCookies,
  createPkce,
  createState,
  decodeJwtPayload,
  endSessionUrl,
  ensureFreshTokens,
  isSingleUserMode,
  readTokens,
  syncSession,
  tokenSetFromResponse,
  writeTokens
} from '$lib/server/auth';
import { FakeCookies, discoveryDocument, jsonResponse, stubFetch } from './helpers';

const authority = 'https://authentik.test/application/o/froststream';

beforeEach(() => {
  vi.stubEnv('AUTH_AUTHORITY', authority);
  vi.stubEnv('API_BASE_URL', 'https://api.test');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('PKCE + state', () => {
  it('creates a base64url S256 challenge from the verifier', () => {
    const { verifier, challenge } = createPkce();
    expect(verifier).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(challenge).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(challenge).not.toEqual(verifier);
  });

  it('creates unique opaque state values', () => {
    expect(createState()).not.toEqual(createState());
  });
});

describe('token cookie round-trip', () => {
  it('writes an httpOnly token cookie and reads it back', () => {
    const cookies = new FakeCookies();
    const tokens = { accessToken: 'a', refreshToken: 'r', expiresAt: Date.now() + 3600_000 };

    writeTokens(cookies as never, tokens, true);

    const recorded = cookies.recorded(authCookies.token)!;
    expect(recorded.options.httpOnly).toBe(true);
    expect(recorded.options.secure).toBe(true);
    expect(readTokens(cookies as never)).toEqual(tokens);
  });

  it('returns null for malformed token cookies', () => {
    const cookies = new FakeCookies({ [authCookies.token]: 'not-json' });
    expect(readTokens(cookies as never)).toBeNull();
  });
});

describe('tokenSetFromResponse', () => {
  it('maps an OIDC token response and computes expiry', () => {
    const before = Date.now();
    const set = tokenSetFromResponse({ access_token: 'a', refresh_token: 'r', id_token: 'i', expires_in: 120 });
    expect(set.accessToken).toBe('a');
    expect(set.refreshToken).toBe('r');
    expect(set.idToken).toBe('i');
    expect(set.expiresAt!).toBeGreaterThanOrEqual(before + 120_000);
  });

  it('throws when access_token is missing', () => {
    expect(() => tokenSetFromResponse({})).toThrow(/access_token/);
  });
});

describe('isSingleUserMode', () => {
  it('honours either env flag', () => {
    expect(isSingleUserMode()).toBe(false);
    vi.stubEnv('VITE_SINGLE_USER_MODE', 'true');
    expect(isSingleUserMode()).toBe(true);
  });
});

describe('ensureFreshTokens', () => {
  it('returns the existing tokens when comfortably before expiry', async () => {
    const cookies = new FakeCookies();
    const tokens = { accessToken: 'a', refreshToken: 'r', expiresAt: Date.now() + 3600_000 };
    writeTokens(cookies as never, tokens, false);
    const fetchMock = stubFetch([]);

    const result = await ensureFreshTokens(cookies as never, false);

    expect(result).toEqual(tokens);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('refreshes via the refresh_token grant when near expiry and persists the rotated tokens', async () => {
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'old', refreshToken: 'r0', expiresAt: Date.now() + 5_000 }, false);

    const fetchMock = stubFetch([
      { match: '.well-known', respond: () => jsonResponse(discoveryDocument) },
      { match: '/token/', respond: () => jsonResponse({ access_token: 'new', refresh_token: 'r1', expires_in: 3600 }) }
    ]);

    const result = await ensureFreshTokens(cookies as never, false);

    expect(result?.accessToken).toBe('new');
    // The rotated set is written back to the cookie.
    expect(readTokens(cookies as never)?.accessToken).toBe('new');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('keeps the previous refresh token when the provider omits a rotated one', async () => {
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'old', refreshToken: 'keep-me', expiresAt: Date.now() + 5_000 }, false);

    stubFetch([
      { match: '.well-known', respond: () => jsonResponse(discoveryDocument) },
      { match: '/token/', respond: () => jsonResponse({ access_token: 'new', expires_in: 3600 }) }
    ]);

    const result = await ensureFreshTokens(cookies as never, false);

    expect(result?.refreshToken).toBe('keep-me');
  });

  it('returns null when there is no session', async () => {
    expect(await ensureFreshTokens(new FakeCookies() as never, false)).toBeNull();
  });
});

describe('endSessionUrl', () => {
  it('builds the RP-initiated logout URL with the id_token hint', async () => {
    stubFetch([{ match: '.well-known', respond: () => jsonResponse(discoveryDocument) }]);

    const url = await endSessionUrl('id-token-abc', 'https://app.test/');

    expect(url).not.toBeNull();
    const parsed = new URL(url!);
    expect(parsed.origin + parsed.pathname).toBe('https://authentik.test/application/o/froststream/end-session/');
    expect(parsed.searchParams.get('post_logout_redirect_uri')).toBe('https://app.test/');
    expect(parsed.searchParams.get('id_token_hint')).toBe('id-token-abc');
  });

  it('returns null when the provider advertises no end_session_endpoint', async () => {
    const { end_session_endpoint, ...withoutEndSession } = discoveryDocument;
    void end_session_endpoint;
    stubFetch([{ match: '.well-known', respond: () => jsonResponse(withoutEndSession) }]);

    expect(await endSessionUrl('id', 'https://app.test/')).toBeNull();
  });
});

describe('syncSession', () => {
  it('POSTs to the WebAPI session endpoint with the bearer token', async () => {
    const fetchMock = stubFetch([{ match: '/api/auth/session', respond: () => jsonResponse({}) }]);

    await syncSession('access-xyz');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [, init] = fetchMock.mock.calls[0];
    expect(init?.method).toBe('POST');
    expect((init?.headers as Record<string, string>).authorization).toBe('Bearer access-xyz');
  });

  it('never throws when the session sync fails', async () => {
    stubFetch([{ match: '/api/auth/session', respond: () => jsonResponse({}, { status: 500 }) }]);
    await expect(syncSession('a')).resolves.toBeUndefined();
  });
});

describe('decodeJwtPayload', () => {
  it('decodes the payload segment of a JWT', () => {
    const payload = Buffer.from(JSON.stringify({ sub: 'abc', groups: ['admins'] })).toString('base64url');
    expect(decodeJwtPayload(`header.${payload}.sig`)).toEqual({ sub: 'abc', groups: ['admins'] });
  });

  it('returns undefined for a malformed token', () => {
    expect(decodeJwtPayload('garbage')).toBeUndefined();
  });
});
