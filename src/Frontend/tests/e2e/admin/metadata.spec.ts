import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('exposes search and database maintenance actions', async ({ page }) => { await page.goto('/admin/metadata'); await expect(page.getByRole('heading', { name: 'Search index' })).toBeVisible(); await expect(page.getByRole('heading', { name: 'Database maintenance reindex' })).toBeVisible(); });
test('requires a media GUID before deletion', async ({ page }) => { await page.goto('/admin/metadata'); await expect(page.getByLabel('Media GUID')).toHaveValue(''); await expect(page.getByRole('button', { name: /Delete media/ })).toBeDisabled(); });
