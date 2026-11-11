import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('shows the empty policies view and switches to bundles', async ({ page }) => { await page.goto('/admin/access-control'); await expect(page.getByText(/No access policies yet/)).toBeVisible(); await page.getByRole('tab', { name: 'Bundles' }).click(); await expect(page.getByRole('tab', { name: 'Bundles' })).toHaveAttribute('aria-selected', 'true'); });
test('reports access-control loading errors', async ({ page }) => { await page.route(url => url.pathname === '/api/global/access-control/policies', route => route.fulfill({ status: 503 })); await page.goto('/admin/access-control'); await expect(page.getByRole('alert')).toBeVisible(); });
