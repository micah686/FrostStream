import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch }) => {
  const [configResponse, statusResponse] = await Promise.all([
    fetch('/api/auth/config'),
    fetch('/auth/status')
  ]);

  const config = configResponse.ok ? await configResponse.json() : null;
  const status = statusResponse.ok
    ? await statusResponse.json()
    : { singleUserMode: false, hasSession: false };

  return {
    config,
    status
  };
};
