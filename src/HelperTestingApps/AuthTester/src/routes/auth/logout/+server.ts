import { redirect } from '@sveltejs/kit';
import {
  clearAuthCookies,
  cookieSecure,
  endSessionUrl,
  isSingleUserMode,
  readTokens
} from '$lib/server/auth';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = async ({ cookies, url }) => {
  const idToken = readTokens(cookies)?.idToken;
  clearAuthCookies(cookies, cookieSecure(url));

  // End the Authentik session too (RP-initiated logout) when the provider supports it; otherwise
  // the local session clear above is sufficient.
  if (!isSingleUserMode()) {
    const target = await endSessionUrl(idToken, `${url.origin}/`);
    if (target) {
      throw redirect(303, target);
    }
  }

  throw redirect(303, '/');
};

export const POST = GET;
