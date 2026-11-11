import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('lists schedules with links to edit', async ({ page }) => { await page.goto('/admin/schedules'); await expect(page.getByText('nightly', { exact: true })).toBeVisible(); await expect(page.getByRole('link', { name: 'Edit schedule nightly' })).toHaveAttribute('href', '/admin/schedules/nightly'); });
test('refreshes schedules', async ({ page }) => { await page.goto('/admin/schedules'); await expect(page.getByText('nightly', { exact: true })).toBeVisible(); const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/global/schedules'); await page.getByRole('button', { name: 'Refresh' }).click(); await request; });
