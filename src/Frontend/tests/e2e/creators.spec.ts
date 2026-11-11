import { test, expect } from '@playwright/test';
import { mockApi } from './support/api';

const source = { id: 1, sourceUrl: 'https://youtube.com/@example/videos', configSetKey: null, scanEnabled: true, incrementalPageSize: 50, consecutiveKnownThreshold: 25, fullRescanIntervalDays: 30, updateCheckIntervalHours: 6, metadataRefreshWindow: 25, lastSuccessfulScanAt: null, lastFullScanAt: null, lastSeenHighWatermark: null, nextFullScanStartIndex: null, createdAt: '2025-01-01T00:00:00Z', lastUpdated: null };
test.beforeEach(async ({ page }) => {
  await mockApi(page, async (route, url) => {
    const method = route.request().method();
    if (url.pathname === '/api/creator-monitor') { await route.fulfill({ json: method === 'GET' ? [source] : { ...source, id: 2, ...route.request().postDataJSON() } }); return true; }
    if (url.pathname === '/api/creator-monitor/1' && method === 'PUT') { await route.fulfill({ json: { ...source, ...route.request().postDataJSON() } }); return true; }
    if (url.pathname === '/api/creator-monitor/1/ignored-media') { await route.fulfill({ json: [] }); return true; }
    return false;
  });
});

test('shows tracked creators and can pause scanning and inspect ignored videos', async ({ page }) => {
  await page.goto('/creators');
  await expect(page.getByRole('heading', { name: '@example' })).toBeVisible();
  await expect(page.getByText('SCANNING', { exact: true })).toBeVisible();
  const update = page.waitForRequest(r => new URL(r.url()).pathname === '/api/creator-monitor/1' && r.method() === 'PUT');
  await page.getByRole('button', { name: 'Pause', exact: true }).click();
  expect((await update).postDataJSON()).toMatchObject({ scanEnabled: false });
  await expect(page.getByRole('button', { name: 'Resume', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Show ignored videos' }).click();
  await expect(page.getByText('No videos have been suppressed by ignore keywords for this creator.')).toBeVisible();
});

test('tracks a new creator and edits scan settings', async ({ page }) => {
  await page.goto('/creators');
  await page.getByRole('button', { name: 'Track creator', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Channel URL').fill('https://youtube.com/@new/videos');
  await dialog.getByLabel('Incremental page size').fill('100');
  const create = page.waitForRequest(r => new URL(r.url()).pathname === '/api/creator-monitor' && r.method() === 'POST');
  await dialog.getByRole('button', { name: 'Track creator' }).click();
  expect((await create).postDataJSON()).toMatchObject({ sourceUrl: 'https://youtube.com/@new/videos', incrementalPageSize: 100, scanEnabled: true });
  await expect(page.getByRole('heading', { name: '@new' })).toBeVisible();
  await page.locator('li').filter({ has: page.getByRole('heading', { name: '@example' }) }).getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('dialog').getByLabel('Channel URL')).toHaveValue(source.sourceUrl);
});

test('reports failed creator loading', async ({ page }) => {
  await page.route(url => url.pathname === '/api/creator-monitor', route => route.fulfill({ status: 503, body: 'Creators unavailable' }));
  await page.goto('/creators');
  await expect(page.getByRole('alert')).toContainText('Creators unavailable');
});
