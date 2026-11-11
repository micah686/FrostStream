import { test, expect } from '@playwright/test';

const hit = (id: string, title: string) => ({
  media: { mediaGuid: id, title, durationSeconds: 120, wasLive: false,
    account: { accountId: 1, accountName: 'Creator', accountHandle: 'creator', platform: 'youtube' } },
  matchedIn: ['subtitles']
});

test.beforeEach(async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const url = new URL(route.request().url());
    let body: unknown = {};
    if (url.pathname === '/api/system/capabilities') body = { deploymentMode: 'Full', accessManagement: { enabled: true }, integrations: {}, backups: {} };
    if (url.pathname === '/api/auth/me') body = { mode: 'multi-user', authenticated: true, profile: { name: 'Tester', initials: 'T', groups: [] } };
    if (url.pathname === '/api/search/status') body = { state: 'ready' };
    if (url.pathname === '/api/search') body = url.searchParams.get('q') === 'missing'
      ? { items: [], totalCount: 0, page: 1, hasMore: false }
      : { items: [url.searchParams.get('page') === '2' ? hit('two', 'Second result') : hit('one', 'First result')], totalCount: 25, page: Number(url.searchParams.get('page')), hasMore: url.searchParams.get('page') !== '2' };
    if (url.pathname === '/api/search/similar/one') body = [hit('two', 'Similar result')];
    if (url.pathname === '/api/metadata/one') body = { title: 'Source video' };
    await route.fulfill({ json: body });
  });
});

test('search results support scope, sorting and pagination', async ({ page }) => {
  await page.goto('/search?q=climate');
  await expect(page.getByRole('heading', { name: 'Results for "climate"' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Play First result' })).toHaveAttribute('href', '/watch/one');
  await expect(page.getByText('25 results')).toBeVisible();
  const scope = page.waitForRequest(r => new URL(r.url()).pathname === '/api/search' && new URL(r.url()).searchParams.get('scope') === 'comments');
  await page.getByRole('navigation', { name: 'Search scope' }).getByRole('button', { name: 'Comments' }).click();
  await scope;
  await expect(page).toHaveURL(/scope=comments/);
  const sorted = page.waitForRequest(r => new URL(r.url()).pathname === '/api/search' && new URL(r.url()).searchParams.get('sortBy') === 'title');
  await page.getByLabel('Sort results').selectOption('title:asc');
  expect(new URL((await sorted).url()).searchParams.get('sortOrder')).toBe('asc');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByText('Page 2 of 2')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Play Second result' })).toBeVisible();
  await page.getByRole('button', { name: 'Previous', exact: true }).click();
  await expect(page.getByText('Page 1 of 2')).toBeVisible();
});

test('handles empty, similar and failed searches', async ({ page }) => {
  await page.goto('/search');
  await expect(page.getByRole('link', { name: 'advanced search builder' })).toHaveAttribute('href', '/search/advanced');
  await page.goto('/search?q=missing');
  await expect(page.getByText('No results for "missing"')).toBeVisible();
  await page.goto('/search?similar=one');
  await expect(page.getByRole('link', { name: '"Source video"' })).toHaveAttribute('href', '/watch/one');
  await expect(page.getByRole('link', { name: 'Play Similar result' })).toBeVisible();
  await page.route(url => url.pathname === '/api/search', route => route.fulfill({ status: 503, body: 'Search unavailable' }));
  await page.goto('/search?q=climate');
  await expect(page.getByRole('alert')).toContainText('Search unavailable');
});

test('advanced builder composes a query, submits it, and resets', async ({ page }) => {
  await page.goto('/search/advanced?q=existing');
  await expect(page.getByLabel('Free text')).toHaveValue('existing');
  await page.getByLabel('Channel / creator').fill('Linus Tech Tips');
  await page.getByLabel('Codec').selectOption('hevc');
  await page.getByLabel('Minimum duration').fill('60');
  await page.getByLabel('Search in').selectOption('subtitles');
  await expect(page.locator('code').filter({ hasText: 'channel:"Linus Tech Tips"' })).toContainText('codec:hevc duration:>=60 existing');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page).toHaveURL(/\/search\?/);
  const url = new URL(page.url());
  expect(url.searchParams.get('q')).toBe('channel:"Linus Tech Tips" codec:hevc duration:>=60 existing');
  expect(url.searchParams.get('scope')).toBe('subtitles');
  await page.goto('/search/advanced');
  await expect(page.getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'codec:h264', exact: true }).click();
  await expect(page.getByLabel('Free text')).toHaveValue('codec:h264');
  await page.getByRole('button', { name: 'Reset' }).click();
  await expect(page.getByLabel('Free text')).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
});
