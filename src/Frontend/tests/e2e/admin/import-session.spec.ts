import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('opens the file selection step', async ({ page }) => { await page.goto('/admin/import/session-1'); await expect(page).toHaveURL(/session-1\/files$/); await expect(page.getByRole('heading', { name: 'File selection' })).toBeVisible(); });
test('keeps the session id in the wizard links', async ({ page }) => { await page.goto('/admin/import/session-1'); await expect(page.getByRole('link', { name: 'Next: metadata' })).toHaveAttribute('href', '/admin/import/session-1/metadata'); });
