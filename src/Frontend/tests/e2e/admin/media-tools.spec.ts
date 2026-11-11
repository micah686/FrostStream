import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('queues a media tools update', async ({ page }) => { await page.route(url => url.pathname === '/api/admin/media-tools/update', route => route.fulfill({ status: 204 })); await page.goto('/admin/media-tools'); const request = page.waitForRequest(r => r.url().endsWith('/media-tools/update') && r.method() === 'POST'); await page.getByRole('button', { name: 'Update binaries' }).click(); await request; await expect(page.getByRole('status')).toContainText('updated successfully'); });
test('shows an update failure', async ({ page }) => { await page.route(url => url.pathname === '/api/admin/media-tools/update', route => route.fulfill({ status: 500 })); await page.goto('/admin/media-tools'); await page.getByRole('button', { name: 'Update binaries' }).click(); await expect(page.getByRole('status')).toContainText('status 500'); });
