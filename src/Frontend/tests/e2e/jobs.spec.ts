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

function queueJob(id: string, overrides: Record<string, unknown> = {}) {
  return {
    jobId: id, correlationId: 'collection-123', status: 'Running', stage: 'MediaAcquire',
    stageStatus: 'Running', runId: 'run-current', runNumber: 2, attempt: 1, maxAttempts: 3,
    sourceUrl: `https://example.com/${id}`, sourceKind: 'Playlist', priority: 50,
    createdAt: '2026-11-14T08:00:00Z', updatedAt: '2026-11-14T08:00:00Z',
    completedAt: null, warningCount: 0, fileSizeBytes: null,
    ...overrides
  };
}

test('collections include subsequent queue pages and open a scoped job list', async ({ page }) => {
  await page.route(url => url.pathname === '/api/downloads/queue', route => {
    const url = new URL(route.request().url());
    const secondPage = url.searchParams.has('cursor');
    return route.fulfill({ json: {
      items: secondPage ? [queueJob('third', { status: 'Failed' })] : [queueJob('first'), queueJob('second', { status: 'Completed' })],
      totalCount: 3, nextCursor: secondPage ? null : 'next-page'
    } });
  });
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'Collections', exact: true }).click();
  await expect(page.getByText('3 jobs', { exact: true })).toBeVisible();
  await expect(page.getByText('1 complete', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Open collection' }).click();
  await expect(page.getByRole('heading', { name: 'third', exact: true })).toBeVisible();
  await page.getByLabel('Job filters').getByRole('button', { name: /Failed/ }).click();
  await expect(page.getByRole('heading', { name: 'third', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'first', exact: true })).toHaveCount(0);
  await page.getByLabel('Breadcrumb').getByRole('button', { name: 'All jobs' }).click();
  await expect(page.getByRole('heading', { name: 'first', exact: true })).toBeVisible();
});

test('workflow details show recorded stages for the current run and retain job controls on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route(url => url.pathname === '/api/downloads/queue', route => route.fulfill({ json: {
    items: [queueJob('first')], totalCount: 1, nextCursor: null
  } }));
  await page.route(url => url.pathname.endsWith('/first/history'), route => route.fulfill({ json: [
    { id: 1, eventName: 'MetadataFetchFailed', payloadJson: JSON.stringify({ Execution: { RunId: 'run-old', Stage: 1 } }), recordedAt: '2026-11-14T07:00:00Z' },
    { id: 2, eventName: 'MetadataFetched', payloadJson: JSON.stringify({ Execution: { RunId: 'run-current', Stage: 1 } }), recordedAt: '2026-11-14T08:00:00Z' }
  ] }));
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Workflow ·/ }).click();
  const details = page.locator('[id="job-details-first"]');
  await details.locator('summary').filter({ hasText: 'Prepare' }).click();
  await expect(details.getByRole('listitem').filter({ hasText: 'Metadata' }).first()).toContainText('Succeeded');
  await page.getByRole('button', { name: 'Runs & IDs', exact: true }).click();
  await expect(details).toContainText('run-current');
  await page.getByRole('button', { name: 'Logs', exact: true }).click();
  await expect(details).toContainText('MetadataFetched');
  const stop = page.waitForRequest(r => r.url().endsWith('/first/stop') && r.method() === 'POST');
  await page.route(url => url.pathname.endsWith('/first/stop'), route => route.fulfill({ status: 202 }));
  await page.getByRole('button', { name: 'Stop job', exact: true }).click();
  await stop;
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '/tmp/frost-jobs-mobile.png', fullPage: true });
});
