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
  return process.env.AUTH_SCOPES || 'openid profile email groups';
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
