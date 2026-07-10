import { error, redirect } from '@sveltejs/kit';
import {
  authCookies,
  clientId,
  clientSecret,
  cookieSecure,
  discover,
  redirectUri,
  safeReturnTo,
  syncSession,
  tokenSetFromResponse,
  writeTokens
} from '$lib/server/auth';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = async ({ cookies, url }) => {
  const expectedState = cookies.get(authCookies.state);
  const verifier = cookies.get(authCookies.verifier);
  const actualState = url.searchParams.get('state');
  const code = url.searchParams.get('code');

  if (!expectedState || !actualState || expectedState !== actualState || !verifier || !code) {
    throw error(400, 'Invalid OIDC callback.');
  }

  const discovery = await discover();
  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    client_id: clientId(),
    code,
    redirect_uri: redirectUri(url.origin),
    code_verifier: verifier
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
    throw error(502, 'OIDC token exchange failed.');
  }

  const tokens = tokenSetFromResponse((await response.json()) as Record<string, unknown>);
  const secure = cookieSecure(url);
  writeTokens(cookies, tokens, secure);
  cookies.delete(authCookies.state, { path: '/', secure });
  cookies.delete(authCookies.verifier, { path: '/', secure });

  // Land back where the visitor was originally headed (stored by /auth/login), or /profile for
  // logins started from the account menu.
  const returnTo = safeReturnTo(cookies.get(authCookies.returnTo));
  cookies.delete(authCookies.returnTo, { path: '/', secure });

  // Upsert the local user and refresh OpenFGA group tuples before landing on the app.
  await syncSession(tokens.accessToken);

  throw redirect(303, returnTo ?? '/profile');
};
