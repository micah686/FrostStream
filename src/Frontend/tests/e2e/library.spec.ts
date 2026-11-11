import { test, expect, type Page } from '@playwright/test';

const media = (id: string, title: string) => ({
  mediaGuid: id, title, durationSeconds: 120, wasLive: false,
  account: { accountId: 42, platform: 'youtube', accountName: 'Example Creator', accountHandle: 'example' }
});
const video = media('video-1', 'First video');
const second = media('video-2', 'Second video');
const paged = (items: unknown[], page = 1, totalCount = items.length, hasMore = false) =>
  ({ items, page, totalCount, hasMore });

async function setup(page: Page) {
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const url = new URL(route.request().url());
    let body: unknown = {};
    switch (url.pathname) {
      case '/api/system/capabilities': body = { deploymentMode: 'Full', accessManagement: { enabled: true }, integrations: {}, backups: {} }; break;
      case '/api/auth/me': body = { mode: 'multi-user', authenticated: true, profile: { name: 'Tester', initials: 'T', groups: [] } }; break;
      case '/api/search/status': body = { state: 'ready' }; break;
      case '/api/global/statistics/overview': body = { inventory: { totalMedia: 25, totalChannels: 1, totalPlaylists: 2, totalDownloads: 3, totalBytes: 1024, totalDurationSeconds: 120 }, watchProgress: { watchedCount: 1, watchedPercent: 4 } }; break;
      case '/api/metadata': body = url.searchParams.get('page') === '2' ? paged([second], 2, 25) : paged([video], 1, 25, true); break;
      case '/api/media/watch-states/history': body = paged([{ media: video, watchState: { positionSeconds: 63, completed: false } }]); break;
      case '/api/media/likes': body = paged([{ media: second, like: { likedAt: '2025-01-01T00:00:00Z' } }]); break;
      case '/api/user/playlists': body = [{ playlistId: 'my list', name: 'Favorites', itemCount: 1, updatedAt: '2025-01-01T00:00:00Z' }, { playlistId: 'empty', name: 'Empty list', itemCount: 0 }]; break;
      case '/api/user/playlists/my%20list': body = { playlistId: 'my list', name: 'Favorites', itemCount: 1, items: [{ mediaGuid: 'video-1' }] }; break;
      case '/api/user/playlists/empty': body = { playlistId: 'empty', name: 'Empty list', itemCount: 0, items: [] }; break;
      case '/api/playlists/library': body = [{ playlistId: 'provider list', title: 'Downloaded list', firstMediaGuid: 'video-2', itemCount: 1 }]; break;
    }
    await route.fulfill({ json: body });
  });
}

test.beforeEach(async ({ page }) => { await setup(page); });

test('shows videos, sorts and paginates the library', async ({ page }) => {
  await page.goto('/library');
  await expect(page.getByRole('heading', { name: 'Library', exact: true })).toBeVisible();
  await expect(page.getByText('25 titles on the server')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Play First video' })).toHaveAttribute('href', '/watch/video-1');
  await expect(page.getByRole('link', { name: "Open Example Creator's channel" })).toHaveAttribute('href', '/channel/42');
  const sortRequest = page.waitForRequest(request => request.url().includes('/api/metadata?') && new URL(request.url()).searchParams.get('sortBy') === 'title');
  await page.getByLabel('Sort library').selectOption('title:asc');
  expect(new URL((await sortRequest).url()).searchParams.get('sortOrder')).toBe('asc');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByText('Page 2 of 2')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Play Second video' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Previous', exact: true }).click();
  await expect(page.getByText('Page 1 of 2')).toBeVisible();
});

test('switches URL-backed tabs and links to history, liked videos and playlists', async ({ page }) => {
  await page.goto('/library?tab=History');
  const nav = page.getByRole('navigation', { name: 'Library sections' });
  await expect(page.getByRole('link', { name: 'Play First video' })).toHaveAttribute('href', '/watch/video-1?t=63');
  await nav.getByRole('button', { name: 'Liked' }).click();
  await expect(page).toHaveURL(/tab=Liked/);
  await expect(page.getByRole('link', { name: 'Play Second video' })).toBeVisible();
  await nav.getByRole('button', { name: 'Playlists' }).click();
  await expect(page.getByRole('link', { name: 'Play playlist Favorites' })).toHaveAttribute('href', '/watch/video-1?ulist=my%20list');
  await expect(page.getByRole('link', { name: 'Edit playlist Empty list' }).first()).toHaveAttribute('href', '/profile/playlists/empty');
  await expect(page.getByRole('link', { name: 'Play playlist Downloaded list' })).toHaveAttribute('href', '/watch/video-2?list=provider%20list');
  await nav.getByRole('button', { name: 'Videos' }).click();
  await expect(page).toHaveURL(/\/library$/);
  await expect(nav.getByRole('button', { name: 'Videos' })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('link', { name: 'Play First video' })).toBeVisible();
});

test('shows empty and failed library states', async ({ page }) => {
  await page.route('**/api/metadata?**', route => route.fulfill({ json: paged([]) }));
  await page.goto('/library');
  await expect(page.getByText('No titles yet')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Go to Download' })).toHaveAttribute('href', '/download');
  await page.route('**/api/metadata?**', route => route.fulfill({ status: 401 }));
  await page.reload();
  await expect(page.getByRole('alert')).toContainText('Your session has expired.');
  await expect(page.getByRole('link', { name: 'Log in again' })).toHaveAttribute('href', '/auth/login');
});
