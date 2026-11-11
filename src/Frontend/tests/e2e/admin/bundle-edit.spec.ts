import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
const bundle = { id: 'user.test', systemOwned: false, endpoints: [], endpointCount: 0, policyCount: 0, memberPolicies: [] };
test.beforeEach(async ({ page }) => adminApi(page, async (route, url) => { if (url.pathname === '/api/global/access-control/bundles') { await route.fulfill({ json: [bundle] }); return true; } return false; }));
test('loads runtime bundle membership', async ({ page }) => { await page.goto('/admin/access-control/bundles/user.test/edit'); await expect(page.getByRole('heading', { name: 'Edit endpoint membership' })).toBeVisible(); await expect(page.getByLabel('Runtime bundle ID')).toHaveValue('test'); });
test('reports a missing runtime bundle', async ({ page }) => { await page.goto('/admin/access-control/bundles/user.missing/edit'); await expect(page.getByRole('alert')).toContainText('Runtime bundle not found'); });
