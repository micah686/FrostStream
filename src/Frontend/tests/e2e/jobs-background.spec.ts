import { test, expect } from '@playwright/test';
import { mockApi } from './support/api';

const run = { runId: 'run-1', taskType: 'search_reindex', scheduleKey: 'nightly', trigger: 'Scheduled', origin: 'server', detail: null, status: 'running', message: 'Indexing', current: 1, total: 2, percent: 50, startedAt: '2025-01-01T00:00:00Z', queuedAt: null, completedAt: null, errorMessage: null, summary: null, log: [] };
test.beforeEach(async ({ page }) => {
  await mockApi(page, async (route, url) => {
    if (url.pathname === '/api/jobs/background') { await route.fulfill({ json: { items: [run], runningCount: 1, queuedCount: 0 } }); return true; }
    return false;
  });
});

test('shows background runs and refreshes the snapshot', async ({ page }) => {
  await page.goto('/jobs/background');
  await expect(page.getByRole('heading', { name: 'Search reindex' })).toBeVisible();
  await expect(page.getByText('1 running')).toBeVisible();
  await expect(page.getByText('Indexing')).toBeVisible();
  const refresh = page.waitForRequest(r => new URL(r.url()).pathname === '/api/jobs/background');
  await page.getByRole('button', { name: 'Refresh' }).click();
  await refresh;
  await expect(page.getByLabel('Jobs sections').getByRole('link', { name: 'Background' })).toHaveAttribute('aria-current', 'page');
});

test('shows an empty background queue', async ({ page }) => {
  await page.route(url => url.pathname === '/api/jobs/background', route => route.fulfill({ json: { items: [], runningCount: 0, queuedCount: 0 } }));
  await page.goto('/jobs/background');
  await expect(page.getByText('No background tasks are running')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Schedules' })).toHaveAttribute('href', '/admin/schedules');
});
