import { error, redirect } from '@sveltejs/kit';
import { configureFrontendAccess, requiresLogin, type SystemCapabilities } from '#lib/frontendAccess.js';
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
  const capabilitiesResponse = await fetch('/api/system/capabilities', {
    credentials: 'same-origin',
    cache: 'no-store'
  });
  if (!capabilitiesResponse.ok) {
    throw error(capabilitiesResponse.status, 'Unable to load FrostStream capabilities.');
  }
  const capabilities = (await capabilitiesResponse.json()) as SystemCapabilities;
  configureFrontendAccess(capabilities);
  const lite = capabilities.deploymentMode === 'Lite';

  const response = await fetch('/api/auth/me', {
    credentials: 'same-origin',
    cache: 'no-store'
  });

  if (requiresLogin(response.status)) {
    const returnTo = `${url.pathname}${url.search}`;
    throw redirect(307, `/auth/login?returnTo=${encodeURIComponent(returnTo)}`);
  }

  if (!response.ok) {
    throw error(response.status, 'Unable to load the FrostStream session.');
  }

  const session = (await response.json()) as AuthMeResponse;
  return {
    capabilities,
    lite,
    accessManagementEnabled: !lite && capabilities.accessManagement.enabled && session.mode !== 'single-user',
    singleUser: session.mode === 'single-user',
    user: session.profile,
    expiresAt: session.expiresAt ? new Date(session.expiresAt).getTime() : null
  };
};
