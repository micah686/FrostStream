import { createHash, randomBytes } from 'node:crypto';
import { env } from '$env/dynamic/private';
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

export interface UserProfile {
  subject?: string;
  name: string;
  username?: string;
  email?: string;
  groups: string[];
  initials: string;
}

interface DiscoveryDocument {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

export const authCookies = {
  token: tokenCookie,
  state: stateCookie,
  verifier: verifierCookie
};

// Config is read through $env/dynamic/private (not process.env) so that a local .env file works
// when the app runs standalone via `pnpm dev`; real environment variables still take precedence.
export function isSingleUserMode(): boolean {
  return isTruthy(env.SINGLE_USER_MODE) || isTruthy(env.VITE_SINGLE_USER_MODE);
}

export function apiBaseUrl(): string {
  return env.API_BASE_URL || env.VITE_API_BASE_URL || 'http://localhost:5041';
}

export function authority(): string {
  return trimTrailingSlash(env.AUTH_AUTHORITY || env.VITE_AUTH_AUTHORITY || '');
}

export function clientId(): string {
  return env.AUTH_CLIENT_ID || 'froststream-bff';
}

export function clientSecret(): string | undefined {
  return nonBlank(env.AUTH_CLIENT_SECRET);
}

export function scopes(): string {
  return env.AUTH_SCOPES || 'openid profile email groups';
}

export function redirectUri(origin: string): string {
  return env.AUTH_REDIRECT_URI || `${origin}/auth/callback`;
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

export function singleUserProfile(): UserProfile {
  const name = env.SINGLE_USER_NAME || 'FrostStream User';
  return {
    name,
    username: 'local',
    groups: ['owner'],
    initials: initialsFrom(name)
  };
}

/**
 * Builds a display profile from the id token (falling back to the access token). The JWT is not
 * verified here: it was issued to this BFF and stored in our own httpOnly cookie, and it is only
 * used for display — the WebAPI verifies signatures on every real request.
 */
export function profileFromTokens(tokens: TokenSet): UserProfile | null {
  const claims = decodeJwtPayload(tokens.idToken) ?? decodeJwtPayload(tokens.accessToken);
  if (!claims) {
    return null;
  }

  const username = stringClaim(claims.preferred_username) ?? stringClaim(claims.nickname);
  const name = stringClaim(claims.name) ?? username ?? stringClaim(claims.email) ?? 'Signed in';
  return {
    subject: stringClaim(claims.sub),
    name,
    username,
    email: stringClaim(claims.email),
    groups: Array.isArray(claims.groups) ? claims.groups.filter((g): g is string => typeof g === 'string') : [],
    initials: initialsFrom(name)
  };
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

function decodeJwtPayload(jwt: string | undefined): Record<string, unknown> | null {
  if (!jwt) {
    return null;
  }

  const parts = jwt.split('.');
  if (parts.length !== 3) {
    return null;
  }

  try {
    return JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8')) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function stringClaim(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
}

function initialsFrom(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return '?';
  }
  const first = words[0][0];
  const last = words.length > 1 ? words[words.length - 1][0] : '';
  return (first + last).toUpperCase();
}
