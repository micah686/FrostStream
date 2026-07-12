import { redirect } from '@sveltejs/kit';
import { isSingleUserMode, profileFromTokens, readTokens } from '$lib/server/auth';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = ({ cookies, params }) => {
  if (isSingleUserMode()) {
    return { key: params.key };
  }

  const tokens = readTokens(cookies);
  const user = tokens ? profileFromTokens(tokens) : null;
  if (!tokens || !user) {
    throw redirect(303, '/auth/login');
  }

  return { key: params.key };
};
