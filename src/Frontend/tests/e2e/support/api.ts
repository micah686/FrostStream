import type { Page, Route } from '@playwright/test';

export async function mockApi(page: Page, handler: (route: Route, url: URL) => Promise<boolean>) {
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const url = new URL(route.request().url());
    if (await handler(route, url)) return;
    if (url.pathname.endsWith('/stream')) return route.fulfill({ status: 503 });
    if (url.pathname === '/api/system/capabilities') return route.fulfill({ json: { deploymentMode: 'Full', accessManagement: { enabled: true }, integrations: {}, backups: {} } });
    if (url.pathname === '/api/auth/me') return route.fulfill({ json: { mode: 'multi-user', authenticated: true, profile: { name: 'Tester', initials: 'T', groups: [] } } });
    if (['/api/user/option-presets', '/api/user/config-sets', '/api/user/cookies', '/api/storage/list'].includes(url.pathname)) return route.fulfill({ json: [] });
    await route.fulfill({ json: {} });
  });
}
