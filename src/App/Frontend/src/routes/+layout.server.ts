import { redirect } from '@sveltejs/kit';
import {
  authority,
  cookieSecure,
  ensureFreshTokens,
  isSingleUserMode,
  profileFromTokens,
  singleUserProfile
} from '$lib/server/auth';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ cookies, url }) => {
  if (isSingleUserMode()) {
    return { singleUser: true, user: singleUserProfile() };
  }

  const tokens = await ensureFreshTokens(cookies, cookieSecure(url));
  const expired = tokens?.expiresAt !== undefined && tokens.expiresAt <= Date.now();

  // No usable session: send the visitor straight to the IdP instead of rendering pages whose API
  // calls would all fail with "Authentication required". The current location rides along so the
  // OIDC callback can land back here. Only when AUTH_AUTHORITY is configured — a standalone
  // `pnpm dev` without it should render (and let /auth/login explain the misconfiguration) rather
  // than turn every page into an error.
  if ((!tokens?.accessToken || expired) && authority()) {
    throw redirect(303, `/auth/login?redirectTo=${encodeURIComponent(url.pathname + url.search)}`);
  }

  return { singleUser: false, user: tokens ? profileFromTokens(tokens) : null };
};
