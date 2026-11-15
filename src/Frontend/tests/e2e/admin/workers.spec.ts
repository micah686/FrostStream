import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('shows worker details and refreshes', async ({ page }) => { await page.goto('/admin/workers'); await expect(page.getByRole('cell', { name: 'Worker One' })).toBeVisible(); await expect(page.getByRole('cell', { name: '/incoming' })).toBeVisible(); const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/admin/workers'); await page.getByRole('button', { name: 'Refresh' }).click(); await request; });
test('shows an empty worker list', async ({ page }) => { await page.route(url => url.pathname === '/api/admin/workers', route => route.fulfill({ json: [] })); await page.goto('/admin/workers'); await expect(page.getByText('No workers have reported yet.')).toBeVisible(); });
test('updates a worker capacity', async ({ page }) => {
  await page.goto('/admin/workers');
  await expect(page.getByRole('cell', { name: '1', exact: true })).toBeVisible();
  const capacity = page.getByRole('spinbutton', { name: 'Maximum active jobs for Worker One' });
  await capacity.fill('3');
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/admin/workers/worker-1/capacity' && r.method() === 'PUT');
  await page.getByRole('button', { name: 'Save' }).click();
  expect((await request).postDataJSON()).toEqual({ workerId: 'worker-1', maxActiveJobs: 3 });
});
