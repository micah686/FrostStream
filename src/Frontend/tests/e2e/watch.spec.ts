import { test, expect } from '@playwright/test';

const detail = {
  mediaGuid: 'one', title: 'Archived video', description: 'A video description', wasLive: false,
  metadataScrapedAt: '2025-01-01T00:00:00Z', tags: ['science'], categories: [], genres: [], cast: [], artists: [], albumArtists: [],
  account: { accountId: 42, accountName: 'Creator', accountHandle: 'creator', isVerified: true }
};

test.beforeEach(async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || url.pathname.startsWith('/stream/'), async route => {
    const url = new URL(route.request().url());
    let body: unknown = {};
    if (url.pathname === '/api/system/capabilities') body = { deploymentMode: 'Full', accessManagement: { enabled: true }, integrations: {}, backups: {} };
    if (url.pathname === '/api/auth/me') body = { mode: 'multi-user', authenticated: true, profile: { name: 'Tester', initials: 'T', groups: [] } };
    if (url.pathname === '/api/metadata/one') body = detail;
    if (url.pathname === '/api/metadata/one/comments') body = { items: [], page: 1, totalCount: 0, hasMore: false };
    if (url.pathname === '/api/metadata') body = { items: [{ mediaGuid: 'two', title: 'Next video', account: { accountName: 'Creator' } }] };
    if (url.pathname.includes('/captions')) body = [];
    if (url.pathname.endsWith('/versions')) body = { versions: [] };
    if (url.pathname.endsWith('/watch-state')) body = { mediaGuid: 'one', completed: false, positionSeconds: 0 };
    if (url.pathname.endsWith('/like-state')) body = { mediaGuid: 'one', liked: false };
    if (url.pathname.endsWith('/like')) body = { mediaGuid: 'one', liked: route.request().method() === 'POST' };
    if (url.pathname.endsWith('/watched')) body = { mediaGuid: 'one', completed: true };
    if (url.pathname.endsWith('/unwatched')) body = { mediaGuid: 'one', completed: false };
    if (url.pathname === '/api/metadata/one/technical') return route.fulfill({ status: 404 });
    if (url.pathname.startsWith('/stream/')) return route.fulfill({ status: 404 });
    await route.fulfill({ json: body });
  });
});

test('shows video details and toggles favorites and watched state', async ({ page }) => {
  await page.goto('/watch/one');
  await expect(page.getByRole('heading', { name: 'Archived video' })).toBeVisible();
  await expect(page.getByRole('link', { name: "Open Creator's channel" })).toHaveAttribute('href', '/channel/42');
  await expect(page.getByText('A video description')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Playback unavailable' })).toBeVisible();
  const like = page.waitForRequest(r => new URL(r.url()).pathname === '/api/media/one/like' && r.method() === 'POST');
  await page.getByRole('button', { name: 'Add to favorites' }).click();
  await like;
  await expect(page.getByRole('button', { name: 'Remove from favorites' })).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Mark watched' }).click();
  await expect(page.getByRole('button', { name: 'Watched', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Watched', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Mark watched' })).toBeVisible();
  await page.getByRole('button', { name: 'More actions' }).click();
  await expect(page.getByRole('menuitem', { name: 'Find similar' })).toHaveAttribute('href', '/search?similar=one');
  await expect(page.getByRole('link', { name: /Next video/ })).toHaveAttribute('href', '/watch/two');
});

test('loads technical metadata on demand and reports missing metadata', async ({ page }) => {
  let requests = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/metadata/one/technical') requests++; });
  await page.goto('/watch/one');
  await expect(page.getByRole('heading', { name: 'Archived video' })).toBeVisible();
  expect(requests).toBe(0);
  await page.getByRole('tab', { name: 'Technical' }).click();
  await expect(page.getByText('No technical metadata was archived for this video.')).toBeVisible();
  await page.getByRole('tab', { name: 'Details' }).click();
  await page.getByRole('tab', { name: 'Technical' }).click();
  expect(requests).toBe(1);
});

test('reports a missing video', async ({ page }) => {
  await page.route(url => url.pathname === '/api/metadata/missing', route => route.fulfill({ status: 404 }));
  await page.goto('/watch/missing');
  await expect(page.getByRole('alert')).toContainText('This video does not exist on the server.');
});

test('bare watch URL displays the not found page', async ({ page }) => {
  await page.goto('/watch');
  await expect(page.getByText('Not Found')).toBeVisible();
});
