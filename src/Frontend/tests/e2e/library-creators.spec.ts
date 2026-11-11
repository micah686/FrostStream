import { test, expect } from '@playwright/test';

const creator = (id: number, name: string, platform: string) => ({
  accountId: id, accountName: name, accountHandle: name.toLowerCase(), platform,
  isVerified: id === 1, mediaCount: id, followerCount: 1200,
  accountUrl: `https://example.com/${id}`, userNote: id === 1 ? 'A favorite creator' : null
});

test.beforeEach(async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const url = new URL(route.request().url());
    let body: unknown = {};
    if (url.pathname === '/api/system/capabilities') body = { deploymentMode: 'Full', accessManagement: { enabled: true }, integrations: {}, backups: {} };
    if (url.pathname === '/api/auth/me') body = { mode: 'multi-user', authenticated: true, profile: { name: 'Tester', initials: 'T', groups: [] } };
    if (url.pathname === '/api/metadata/accounts') {
      const filtered = url.searchParams.get('platform');
      const after = url.searchParams.get('after');
      body = filtered ? { items: [], nextCursor: null, hasMore: false }
        : after ? { items: [creator(2, 'Bob', 'twitch')], nextCursor: null, hasMore: false }
        : { items: [creator(1, 'Alice', 'youtube')], nextCursor: 'cursor-1', hasMore: true };
    }
    await route.fulfill({ json: body });
  });
});

test('shows creator details and navigates cursor pages', async ({ page }) => {
  await page.goto('/library/creators');
  await expect(page.getByRole('heading', { name: 'Creators', exact: true })).toBeVisible();
  await expect(page.getByText('1 creator on this page')).toBeVisible();
  await expect(page.getByText('@alice')).toBeVisible();
  await expect(page.getByTitle('Verified')).toBeVisible();
  await expect(page.getByText('A favorite creator')).toBeVisible();
  await expect(page.getByRole('link', { name: 'View channel' })).toHaveAttribute('href', '/channel/1');
  await expect(page.getByRole('link', { name: 'Source' })).toHaveAttribute('href', 'https://example.com/1');
  const request = page.waitForRequest(request => new URL(request.url()).searchParams.get('after') === 'cursor-1');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await request;
  await expect(page.getByText('Page 2', { exact: true })).toBeVisible();
  await expect(page.getByText('@bob')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Previous', exact: true }).click();
  await expect(page.getByText('@alice')).toBeVisible();
});

test('applies and clears a platform filter and refreshes', async ({ page }) => {
  await page.goto('/library/creators');
  await expect(page.getByText('@alice')).toBeVisible();
  await page.getByLabel('Platform').fill('  twitch  ');
  const request = page.waitForRequest(request => new URL(request.url()).searchParams.get('platform') === 'twitch');
  await page.getByRole('button', { name: 'Apply' }).click();
  await request;
  await expect(page.getByText('No creators found')).toBeVisible();
  await expect(page.getByText('filtered to twitch')).toBeVisible();
  await page.getByRole('button', { name: 'Clear' }).click();
  await expect(page.getByLabel('Platform')).toHaveValue('');
  await expect(page.getByText('@alice')).toBeVisible();
  const refresh = page.waitForRequest(request => new URL(request.url()).pathname === '/api/metadata/accounts');
  await page.getByRole('button', { name: 'Refresh' }).click();
  await refresh;
  await expect(page.getByText('@alice')).toBeVisible();
});

test('shows a failed request and can recover by refreshing', async ({ page }) => {
  await page.route('**/api/metadata/accounts?**', route => route.fulfill({ status: 500, body: 'Accounts unavailable' }));
  await page.goto('/library/creators');
  await expect(page.getByRole('alert')).toContainText('Accounts unavailable');
  await page.unroute('**/api/metadata/accounts?**');
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByText('@alice')).toBeVisible();
});
