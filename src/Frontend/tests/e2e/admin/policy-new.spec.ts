import { test, expect } from '@playwright/test';
import { adminApi } from '../support/admin';
test.beforeEach(async ({ page }) => adminApi(page));
test('opens the policy editor through the new-policy route', async ({ page }) => { await page.goto('/admin/access-control/policies/new'); await expect(page).toHaveURL(/view=new-policy/); await expect(page.getByRole('heading', { name: 'Create policy' })).toBeVisible(); });
test('normalizes a new policy name', async ({ page }) => { await page.goto('/admin/access-control/policies/new'); await page.getByPlaceholder('family-viewing').fill('Family Policy!'); await expect(page.getByPlaceholder('family-viewing')).toHaveValue('familypolicy'); });
