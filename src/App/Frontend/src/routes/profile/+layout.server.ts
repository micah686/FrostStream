import { redirect } from '@sveltejs/kit';
import {
  isSingleUserMode,
  profileFromTokens,
  readTokens,
  singleUserProfile
} from '$lib/server/auth';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = ({ cookies }) => {
  if (isSingleUserMode()) {
    return { singleUser: true, user: singleUserProfile(), expiresAt: null };
  }

  const tokens = readTokens(cookies);
  const user = tokens ? profileFromTokens(tokens) : null;
  if (!tokens || !user) {
    throw redirect(303, '/auth/login');
  }

  return { singleUser: false, user, expiresAt: tokens.expiresAt ?? null };
};
