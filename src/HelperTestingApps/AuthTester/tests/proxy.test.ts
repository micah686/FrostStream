import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GET } from '../src/routes/api/[...path]/+server';
import { authCookies, writeTokens } from '$lib/server/auth';
import { FakeCookies, discoveryDocument, jsonResponse, stubFetch } from './helpers';

beforeEach(() => {
  vi.stubEnv('AUTH_AUTHORITY', 'https://authentik.test/application/o/froststream');
  vi.stubEnv('API_BASE_URL', 'https://api.test');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function event(cookies: FakeCookies, path: string) {
  return {
    request: new Request(`https://app.test/api/${path}`, { method: 'GET' }),
    params: { path },
    url: new URL(`https://app.test/api/${path}`),
    cookies
  } as never;
}

describe('GET /api/[...path] proxy (multi-user)', () => {
  it('attaches the bearer token to the upstream request', async () => {
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'live-token', expiresAt: Date.now() + 3600_000 }, true);
    let upstreamAuth: string | null = null;

    stubFetch([
      {
        match: 'api.test/api/metadata',
        respond: (_url, init) => {
          upstreamAuth = (init?.headers as Headers).get('authorization');
          return jsonResponse({ ok: true });
        }
      }
    ]);

    const response = await GET(event(cookies, 'metadata'));

    expect(response.status).toBe(200);
    expect(upstreamAuth).toBe('Bearer live-token');
  });

  it('refreshes a near-expiry token and forwards the rotated one', async () => {
    const cookies = new FakeCookies();
    writeTokens(cookies as never, { accessToken: 'stale', refreshToken: 'r0', expiresAt: Date.now() + 5_000 }, false);
    let upstreamAuth: string | null = null;

    stubFetch([
      { match: '.well-known', respond: () => jsonResponse(discoveryDocument) },
      { match: '/token/', respond: () => jsonResponse({ access_token: 'rotated', expires_in: 3600 }) },
      {
        match: 'api.test/api/metadata',
        respond: (_url, init) => {
          upstreamAuth = (init?.headers as Headers).get('authorization');
          return jsonResponse({ ok: true });
        }
      }
    ]);

    const response = await GET(event(cookies, 'metadata'));

    expect(response.status).toBe(200);
    expect(upstreamAuth).toBe('Bearer rotated');
  });

  it('returns 401 without proxying when there is no session', async () => {
    const fetchMock = stubFetch([]);

    const response = await GET(event(new FakeCookies(), 'metadata'));

    expect(response.status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('proxies /api/auth/config anonymously, without requiring or attaching a token', async () => {
    let upstreamAuth: string | null = 'unset';
    stubFetch([
      {
        match: 'api.test/api/auth/config',
        respond: (_url, init) => {
          upstreamAuth = (init?.headers as Headers).get('authorization');
          return jsonResponse({ mode: 'multi-user' });
        }
      }
    ]);

    const response = await GET(event(new FakeCookies(), 'auth/config'));

    expect(response.status).toBe(200);
    expect(upstreamAuth).toBeNull();
  });
});

describe('GET /api/[...path] proxy (single-user)', () => {
  it('forwards without a bearer token', async () => {
    vi.stubEnv('SINGLE_USER_MODE', 'true');
    let upstreamAuth: string | null = 'unset';
    stubFetch([
      {
        match: 'api.test/api/metadata',
        respond: (_url, init) => {
          upstreamAuth = (init?.headers as Headers).get('authorization');
          return jsonResponse({ ok: true });
        }
      }
    ]);

    const response = await GET(event(new FakeCookies(), 'metadata'));

    expect(response.status).toBe(200);
    expect(upstreamAuth).toBeNull();
  });
});
