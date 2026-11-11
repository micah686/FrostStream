import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('shows a storage target and its path', async ({ page }) => { await page.goto('/admin/storage/archive'); await expect(page.getByRole('heading', { name: 'archive' })).toBeVisible(); await expect(page.getByText('/media/archive')).toBeVisible(); });
test('reports a missing target', async ({ page }) => { await page.route(url => url.pathname === '/api/storage/missing', route => route.fulfill({ status: 404 })); await page.goto('/admin/storage/missing'); await expect(page.getByRole('alert')).toContainText('404'); });
