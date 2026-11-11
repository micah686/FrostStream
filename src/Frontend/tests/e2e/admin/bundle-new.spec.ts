import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('shows the runtime bundle editor', async ({ page }) => { await page.goto('/admin/access-control/bundles/new'); await expect(page.getByRole('heading', { name: 'Create runtime bundle' })).toBeVisible(); await expect(page.getByLabel('Bundle id after the user. prefix')).toBeVisible(); });
test('validates an empty bundle id', async ({ page }) => { await page.goto('/admin/access-control/bundles/new'); await page.getByRole('button', { name: 'Create bundle' }).click(); await expect(page.getByRole('alert')).toBeVisible(); });
