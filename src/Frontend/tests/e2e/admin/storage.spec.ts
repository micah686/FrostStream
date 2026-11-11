import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('lists storage targets and links to their details', async ({ page }) => { await page.goto('/admin/storage'); await expect(page.getByText('Archive storage')).toBeVisible(); await expect(page.getByRole('link', { name: 'View storage target archive' })).toHaveAttribute('href', '/admin/storage/archive'); await expect(page.getByRole('link', { name: /Register storage/ })).toHaveAttribute('href', '/admin/storage/new'); });
test('shows an empty storage list', async ({ page }) => { await page.route(url => url.pathname === '/api/storage/list', route => route.fulfill({ json: [] })); await page.goto('/admin/storage'); await expect(page.getByText(/No storage targets/)).toBeVisible(); });
