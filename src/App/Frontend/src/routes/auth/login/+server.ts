import { redirect } from '@sveltejs/kit';
import {
  authCookies,
  clientId,
  cookieSecure,
  createPkce,
  createState,
  discover,
  isSingleUserMode,
  redirectUri,
  scopes,
  setTransientCookie
} from '$lib/server/auth';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = async ({ cookies, url }) => {
  if (isSingleUserMode()) {
    throw redirect(303, '/');
  }

  const discovery = await discover();
  const state = createState();
  const pkce = createPkce();
  const secure = cookieSecure(url);
  setTransientCookie(cookies, authCookies.state, state, secure);
  setTransientCookie(cookies, authCookies.verifier, pkce.verifier, secure);

  const authorize = new URL(discovery.authorization_endpoint);
  authorize.searchParams.set('client_id', clientId());
  authorize.searchParams.set('redirect_uri', redirectUri(url.origin));
  authorize.searchParams.set('response_type', 'code');
  authorize.searchParams.set('scope', scopes());
  authorize.searchParams.set('state', state);
  authorize.searchParams.set('code_challenge', pkce.challenge);
  authorize.searchParams.set('code_challenge_method', 'S256');

  throw redirect(303, authorize.toString());
};
