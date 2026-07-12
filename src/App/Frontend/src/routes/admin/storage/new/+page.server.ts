import { redirect } from '@sveltejs/kit';
import { isSingleUserMode, profileFromTokens, readTokens } from '$lib/server/auth';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = ({ cookies }) => {
  if (isSingleUserMode()) {
    return {};
  }

  const tokens = readTokens(cookies);
  const user = tokens ? profileFromTokens(tokens) : null;
  if (!tokens || !user) {
    throw redirect(303, '/auth/login');
  }

  return {};
};
