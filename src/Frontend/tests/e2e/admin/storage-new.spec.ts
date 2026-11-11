import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('switches storage provider forms', async ({ page }) => { await page.goto('/admin/storage/new'); await expect(page.getByRole('heading', { name: 'Register storage' })).toBeVisible(); await page.getByRole('radiogroup', { name: 'Storage type' }).getByRole('radio', { name: /Network/ }).click(); await expect(page.getByLabel('Host', { exact: true })).toBeVisible(); });
test('requires local storage key and path', async ({ page }) => { await page.goto('/admin/storage/new'); await expect(page.getByLabel('Key', { exact: true })).toHaveValue(''); await expect(page.getByLabel('Path', { exact: true })).toHaveValue(''); await expect(page.getByRole('link', { name: 'Back', exact: true })).toHaveAttribute('href', '/admin/storage'); });
