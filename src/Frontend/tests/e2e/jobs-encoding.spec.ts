import { test, expect } from '@playwright/test';
import { mockApi } from './support/api';

const item = { renditionId: 'enc-1', kind: 'Stream', mediaGuid: 'video-1', title: 'Encoded video', sourceVersion: 1, status: 'Failed', storageKey: 'default', storagePath: null, sizeBytes: null, durationSeconds: null, errorMessage: 'Encoder failed', createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z' };
test.beforeEach(async ({ page }) => {
  await mockApi(page, async (route, url) => {
    if (url.pathname === '/api/media/renditions/queue') { await route.fulfill({ json: { items: [item], totalCount: 1, nextCursor: null } }); return true; }
    return false;
  });
});

test('shows encoding results and applies filters', async ({ page }) => {
  await page.goto('/jobs/encoding');
  await expect(page.getByRole('heading', { name: 'Encoded video' })).toBeVisible();
  await expect(page.getByText('Encoder failed')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Watch', exact: true })).toHaveAttribute('href', '/watch/video-1');
  const kind = page.waitForRequest(r => new URL(r.url()).pathname === '/api/media/renditions/queue' && new URL(r.url()).searchParams.get('kind') === 'Audio');
  await page.getByLabel('Encoding kind filters').getByRole('button', { name: 'Audio (Opus)' }).click();
  await kind;
  const status = page.waitForRequest(r => new URL(r.url()).pathname === '/api/media/renditions/queue' && new URL(r.url()).searchParams.get('status') === 'Failed');
  await page.getByLabel('Encoding status filters').getByRole('button', { name: 'Failed' }).click();
  await status;
  const search = page.waitForRequest(r => new URL(r.url()).pathname === '/api/media/renditions/queue' && new URL(r.url()).searchParams.get('q') === 'video');
  await page.getByLabel('Search encoding jobs').fill('video');
  await search;
  await expect(page.getByLabel('Jobs sections').getByRole('link', { name: 'Encoding' })).toHaveAttribute('aria-current', 'page');
});

test('shows the empty encoding queue', async ({ page }) => {
  await page.route(url => url.pathname === '/api/media/renditions/queue', route => route.fulfill({ json: { items: [], totalCount: 0, nextCursor: null } }));
  await page.goto('/jobs/encoding');
  await expect(page.getByText('No encoding jobs match this view')).toBeVisible();
});
