import { createHash, randomBytes } from 'node:crypto';
import type { Cookies } from '@sveltejs/kit';

const tokenCookie = 'fs_auth_tokens';
const stateCookie = 'fs_oidc_state';
const verifierCookie = 'fs_oidc_verifier';

export interface TokenSet {
  accessToken: string;
  refreshToken?: string;
  idToken?: string;
  expiresAt?: number;
}

interface DiscoveryDocument {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

// Refresh the access token this many ms before it actually expires, so in-flight proxied
// requests never carry a token that lapses mid-flight.
const refreshSkewMs = 60_000;

export const authCookies = {
  token: tokenCookie,
  state: stateCookie,
  verifier: verifierCookie
};

export function isSingleUserMode(): boolean {
  return isTruthy(process.env.SINGLE_USER_MODE) || isTruthy(process.env.VITE_SINGLE_USER_MODE);
}

export function apiBaseUrl(): string {
  return process.env.API_BASE_URL || process.env.VITE_API_BASE_URL || 'https://localhost:7243';
}

export function authority(): string {
  return trimTrailingSlash(process.env.AUTH_AUTHORITY || process.env.VITE_AUTH_AUTHORITY || '');
}

export function clientId(): string {
  return process.env.AUTH_CLIENT_ID || 'froststream-bff';
}

export function clientSecret(): string | undefined {
  return nonBlank(process.env.AUTH_CLIENT_SECRET);
}

export function scopes(): string {
  // offline_access is required for Authentik to issue a refresh token.
  return process.env.AUTH_SCOPES || 'openid profile email groups offline_access';
}

export function redirectUri(origin: string): string {
  return process.env.AUTH_REDIRECT_URI || `${origin}/auth/callback`;
}

export async function discover(): Promise<DiscoveryDocument> {
  const issuer = authority();
  if (!issuer) {
    throw new Error('AUTH_AUTHORITY is required when single-user mode is disabled.');
  }

  const response = await fetch(`${issuer}/.well-known/openid-configuration`);
  if (!response.ok) {
    throw new Error(`OIDC discovery failed with status ${response.status}.`);
  }

  return response.json() as Promise<DiscoveryDocument>;
}

export function createPkce() {
  const verifier = base64Url(randomBytes(32));
  const challenge = base64Url(createHash('sha256').update(verifier).digest());
  return { verifier, challenge };
}

export function createState(): string {
  return base64Url(randomBytes(32));
}

export function readTokens(cookies: Cookies): TokenSet | null {
  const raw = cookies.get(tokenCookie);
  if (!raw) {
    return null;
  }

  try {
    return JSON.parse(raw) as TokenSet;
  } catch {
    return null;
  }
}

export function writeTokens(cookies: Cookies, tokens: TokenSet, secure: boolean): void {
  const maxAge = tokens.expiresAt ? Math.max(60, Math.floor((tokens.expiresAt - Date.now()) / 1000)) : 3600;
  cookies.set(tokenCookie, JSON.stringify(tokens), {
    httpOnly: true,
    sameSite: 'lax',
    secure,
    path: '/',
    maxAge
  });
}

export function clearAuthCookies(cookies: Cookies, secure: boolean): void {
  for (const name of [tokenCookie, stateCookie, verifierCookie]) {
    cookies.delete(name, {
      httpOnly: true,
      sameSite: 'lax',
      secure,
      path: '/'
    });
  }
}

export function cookieSecure(url: URL): boolean {
  return url.protocol === 'https:';
}

export function setTransientCookie(cookies: Cookies, name: string, value: string, secure: boolean): void {
  cookies.set(name, value, {
    httpOnly: true,
    sameSite: 'lax',
    secure,
    path: '/',
    maxAge: 300
  });
}

export function tokenSetFromResponse(body: Record<string, unknown>): TokenSet {
  const accessToken = typeof body.access_token === 'string' ? body.access_token : '';
  if (!accessToken) {
    throw new Error('Token response did not include an access_token.');
  }

  const expiresIn = typeof body.expires_in === 'number' ? body.expires_in : 3600;
  return {
    accessToken,
    refreshToken: typeof body.refresh_token === 'string' ? body.refresh_token : undefined,
    idToken: typeof body.id_token === 'string' ? body.id_token : undefined,
    expiresAt: Date.now() + expiresIn * 1000
  };
}

/**
 * Notifies the WebAPI that a session was established so it can upsert the local user and refresh
 * OpenFGA group tuples. Best-effort: never throw, so a sync hiccup cannot break login.
 */
export async function syncSession(accessToken: string): Promise<void> {
  try {
    const response = await fetch(new URL('/api/auth/session', apiBaseUrl()), {
      method: 'POST',
      headers: { authorization: `Bearer ${accessToken}` }
    });
    if (!response.ok) {
      console.warn(`Session sync returned ${response.status}.`);
    }
  } catch (err) {
    console.warn('Session sync failed:', err);
  }
}

/**
 * Returns a valid token set, transparently refreshing it via the OIDC refresh_token grant when the
 * access token is within {@link refreshSkewMs} of expiry. Persists the rotated tokens back to the
 * httpOnly cookie. Returns null when there is no usable session.
 */
export async function ensureFreshTokens(cookies: Cookies, secure: boolean): Promise<TokenSet | null> {
  const tokens = readTokens(cookies);
  if (!tokens?.accessToken) {
    return null;
  }

  const expiresAt = tokens.expiresAt ?? 0;
  if (!tokens.refreshToken || expiresAt - Date.now() > refreshSkewMs) {
    return tokens;
  }

  try {
    const discovery = await discover();
    const body = new URLSearchParams({
      grant_type: 'refresh_token',
      client_id: clientId(),
      refresh_token: tokens.refreshToken
    });
    const secret = clientSecret();
    if (secret) {
      body.set('client_secret', secret);
    }

    const response = await fetch(discovery.token_endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body
    });

    if (!response.ok) {
      console.warn(`Token refresh failed with status ${response.status}.`);
      return tokens;
    }

    const refreshed = tokenSetFromResponse((await response.json()) as Record<string, unknown>);
    // Authentik may omit a rotated refresh token; keep the previous one when it does.
    if (!refreshed.refreshToken) {
      refreshed.refreshToken = tokens.refreshToken;
    }
    writeTokens(cookies, refreshed, secure);
    return refreshed;
  } catch (err) {
    console.warn('Token refresh error:', err);
    return tokens;
  }
}

/**
 * Builds the IdP end-session URL (RP-initiated logout) when discovery advertises one. Returns null
 * if the provider has no end_session_endpoint, in which case logout is local-only.
 */
export async function endSessionUrl(idToken: string | undefined, postLogoutRedirect: string): Promise<string | null> {
  try {
    const discovery = await discover();
    if (!discovery.end_session_endpoint) {
      return null;
    }

    const endSession = new URL(discovery.end_session_endpoint);
    endSession.searchParams.set('post_logout_redirect_uri', postLogoutRedirect);
    if (idToken) {
      endSession.searchParams.set('id_token_hint', idToken);
    }
    return endSession.toString();
  } catch (err) {
    console.warn('Failed building end-session URL:', err);
    return null;
  }
}

export function decodeJwtPayload(token: string | undefined): Record<string, unknown> | undefined {
  const payload = token?.split('.')[1];
  if (!payload) {
    return undefined;
  }

  try {
    return JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as Record<string, unknown>;
  } catch {
    return undefined;
  }
}

function isTruthy(value: string | undefined): boolean {
  return value === '1' || value?.toLowerCase() === 'true' || value?.toLowerCase() === 'yes' || value?.toLowerCase() === 'on';
}

function trimTrailingSlash(value: string): string {
  return value.endsWith('/') ? value.slice(0, -1) : value;
}

function nonBlank(value: string | undefined): string | undefined {
  return value && value.trim().length > 0 ? value : undefined;
}

function base64Url(value: Buffer): string {
  return value.toString('base64url');
}
