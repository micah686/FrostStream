import { error, redirect } from '@sveltejs/kit';
import type { LayoutLoad } from './$types';

export const ssr = false;

interface AuthProfile {
  subject: string;
  name: string;
  username?: string;
  email?: string;
  groups: string[];
  initials: string;
}

interface AuthMeResponse {
  mode: 'single-user' | 'multi-user';
  authenticated: boolean;
  profile: AuthProfile;
  expiresAt?: string | null;
}

export const load: LayoutLoad = async ({ fetch, url }) => {
  const response = await fetch('/api/auth/me', {
    credentials: 'same-origin',
    cache: 'no-store'
  });

  if (response.status === 401) {
    const returnTo = `${url.pathname}${url.search}`;
    throw redirect(307, `/auth/login?returnTo=${encodeURIComponent(returnTo)}`);
  }

  if (!response.ok) {
    throw error(response.status, 'Unable to load the FrostStream session.');
  }

  const session = (await response.json()) as AuthMeResponse;
  return {
    singleUser: session.mode === 'single-user',
    user: session.profile,
    expiresAt: session.expiresAt ? new Date(session.expiresAt).getTime() : null
  };
};
