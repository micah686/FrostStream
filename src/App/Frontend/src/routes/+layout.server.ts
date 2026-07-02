import {
  isSingleUserMode,
  profileFromTokens,
  readTokens,
  singleUserProfile
} from '$lib/server/auth';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = ({ cookies }) => {
  if (isSingleUserMode()) {
    return { singleUser: true, user: singleUserProfile() };
  }

  const tokens = readTokens(cookies);
  return { singleUser: false, user: tokens ? profileFromTokens(tokens) : null };
};
