import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('explains the import workflow', async ({ page }) => { await page.goto('/admin/import'); await expect(page.getByRole('heading', { name: 'Import local media' })).toBeVisible(); await expect(page.getByText(/worker’s incoming folder/)).toBeVisible(); });
test('starts the source selection wizard', async ({ page }) => { await page.goto('/admin/import'); await page.getByRole('link', { name: 'Start import' }).click(); await expect(page).toHaveURL(/\/admin\/import\/new\/source$/); await expect(page.getByRole('heading', { name: 'Source selection' })).toBeVisible(); });
