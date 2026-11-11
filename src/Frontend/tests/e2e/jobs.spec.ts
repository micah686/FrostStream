import { test, expect } from '@playwright/test';
import { mockApi } from './support/api';

test.beforeEach(async ({ page }) => {
  await mockApi(page, async (route, url) => {
    if (url.pathname === '/api/downloads/queue') { await route.fulfill({ json: { items: [], totalCount: 0, nextCursor: null } }); return true; }
    return false;
  });
});

test('filters and searches the download queue', async ({ page }) => {
  await page.goto('/jobs');
  await expect(page.getByText('No jobs match this view')).toBeVisible();
  const sourceRequest = page.waitForRequest(r => new URL(r.url()).pathname === '/api/downloads/queue' && new URL(r.url()).searchParams.get('sourceKind') === 'Playlist');
  await page.getByLabel('Job source filters').getByRole('button', { name: 'Playlists' }).click();
  await sourceRequest;
  const filterRequest = page.waitForRequest(r => new URL(r.url()).pathname === '/api/downloads/queue' && new URL(r.url()).searchParams.get('stateGroup') === 'queued');
  await page.getByLabel('Job filters').getByRole('button', { name: /Queued/ }).click();
  expect(new URL((await filterRequest).url()).searchParams.get('sort')).toBe('priority');
  const search = page.waitForRequest(r => new URL(r.url()).pathname === '/api/downloads/queue' && new URL(r.url()).searchParams.get('q') === 'example');
  await page.getByLabel('Search jobs').fill('example');
  await search;
  await expect(page.getByLabel('Jobs sections').getByRole('link', { name: 'Downloads' })).toHaveAttribute('aria-current', 'page');
});

test('clean up dialog can be cancelled', async ({ page }) => {
  await page.goto('/jobs');
  await expect(page.getByText('No jobs match this view')).toBeVisible();
  await page.getByRole('button', { name: 'Clean up', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(dialog).not.toBeVisible();
});

test('displays queue errors', async ({ page }) => {
  await page.route(url => url.pathname === '/api/downloads/queue', route => route.fulfill({ status: 503, body: 'Queue unavailable' }));
  await page.goto('/jobs');
  await expect(page.getByRole('alert')).toContainText('status 503');
});
