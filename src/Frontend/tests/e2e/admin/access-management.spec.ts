import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('redirects to the current admin page', async ({ page }) => { await page.goto('/admin/access-management'); await expect(page).toHaveURL('http://127.0.0.1:25001/admin/access-control'); });
test('renders the destination after redirect', async ({ page }) => { await page.goto('/admin/access-management'); await expect(page.getByRole('heading', { name: 'Access control', exact: true })).toBeVisible(); });
