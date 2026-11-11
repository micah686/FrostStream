import { test, expect } from '@playwright/test';
import { mockApi } from './support/api';

test.beforeEach(async ({ page }) => {
  await mockApi(page, async (route, url) => {
    if (url.pathname === '/api/user/config-sets') { await route.fulfill({ json: [{ key: 'archive', name: 'Archive' }] }); return true; }
    if (url.pathname === '/api/downloads/video') { await route.fulfill({ json: { jobId: 'job-1' } }); return true; }
    if (url.pathname === '/api/playlists') { await route.fulfill({ json: { playlistId: 'list-1', correlationId: 'group-1' } }); return true; }
    if (url.pathname === '/api/creator-monitor/channel-downloads') { await route.fulfill({ json: { correlationId: 'channel-1' } }); return true; }
    return false;
  });
});

test('queues a single download with explicit options', async ({ page }) => {
  await page.goto('/download');
  await page.getByLabel('Source URL').fill('https://example.com/video');
  await page.getByLabel('Worker Tags').fill('gpu, local');
  await page.getByLabel('Fetch comments').check();
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/downloads/video' && r.method() === 'POST');
  await page.getByRole('button', { name: 'Queue download' }).click();
  expect((await request).postDataJSON()).toMatchObject({ sourceUrl: 'https://example.com/video', tags: ['gpu', 'local'], fetchComments: true });
  await expect(page.getByLabel('Source URL')).toHaveValue('');
  await expect(page.getByText('Queued this session')).toBeVisible();
  await expect(page.getByText('https://example.com/video')).toBeVisible();
});

test('config set disables conflicting options and submits its key', async ({ page }) => {
  await page.goto('/download');
  await page.getByLabel('Config set').selectOption('archive');
  await expect(page.getByLabel('Worker Tags')).toBeDisabled();
  await expect(page.getByLabel('Fetch comments')).toBeDisabled();
  await page.getByLabel('Source URL').fill('https://example.com/video');
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/downloads/video' && r.method() === 'POST');
  await page.getByRole('button', { name: 'Queue download' }).click();
  expect((await request).postDataJSON()).toMatchObject({ configSetKey: 'archive', storageKey: null });
});

test('queues playlists and creator channels', async ({ page }) => {
  await page.goto('/download');
  await page.getByRole('tab', { name: 'Playlist' }).click();
  await page.getByLabel('Playlist URL').fill('https://example.com/list');
  await page.getByLabel('Encode for playlist').check();
  const list = page.waitForRequest(r => new URL(r.url()).pathname === '/api/playlists' && r.method() === 'POST');
  await page.getByRole('button', { name: 'Queue playlist' }).click();
  expect((await list).postDataJSON()).toMatchObject({ sourceUrl: 'https://example.com/list', encodeForPlaylist: true });
  await page.getByRole('tab', { name: 'Creator' }).click();
  await page.getByLabel('Channel URL').fill('https://example.com/channel');
  const creator = page.waitForRequest(r => new URL(r.url()).pathname === '/api/creator-monitor/channel-downloads');
  await page.getByRole('button', { name: 'Queue channel download' }).click();
  expect((await creator).postDataJSON()).toMatchObject({ sourceUrl: 'https://example.com/channel' });
  await expect(page.getByText('https://example.com/channel')).toBeVisible();
});

test('reports a failed download without clearing the URL', async ({ page }) => {
  await page.route(url => url.pathname === '/api/downloads/video', route => route.fulfill({ status: 500, json: { detail: 'Queue unavailable' } }));
  await page.goto('/download');
  await page.getByLabel('Source URL').fill('https://example.com/video');
  await page.getByRole('button', { name: 'Queue download' }).click();
  await expect(page.getByRole('alert')).toContainText('Queue unavailable');
  await expect(page.getByLabel('Source URL')).toHaveValue('https://example.com/video');
});
