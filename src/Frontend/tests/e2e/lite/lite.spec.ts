import { expect, test } from '@playwright/test';

async function controlPost(page: import('@playwright/test').Page, route: string, data?: unknown) {
  return page.request.post(`${process.env.FROSTSTREAM_E2E_LITE_CONTROL_URL}${route}`, {
    data,
    headers: { authorization: `Bearer ${process.env.FROSTSTREAM_E2E_LITE_CONTROL_TOKEN}` }
  });
}

async function createPlaylist(page: import('@playwright/test').Page, name: string) {
  await page.goto('/profile/playlists/new');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Create playlist' }).click();
  await expect(page.getByRole('heading', { name })).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto('/library');
  await expect(page.getByRole('link', { name: 'Library' }).first()).toBeVisible();
  const caps = await page.request.get('/api/system/capabilities');
  expect(caps.ok(), 'Lite capabilities endpoint must be ready').toBeTruthy();
  expect((await caps.json()).deploymentMode).toBe('Lite');
  expect(errors, 'Browser must not report page or console errors').toEqual([]);
  expect(await page.locator('body').innerText()).toContain('Library');
});

test('UI-LITE-E-001: fresh browser opens Lite deep links as local Admin', async ({ browser, baseURL }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  for (const route of ['/profile', '/library', '/admin']) {
    await page.goto(new URL(route, baseURL).toString());
    await expect(page).not.toHaveURL(/auth\/login/);
    await expect(page.getByRole('link', { name: 'Open profile for Admin' })).toBeVisible();
  }
  await expect(page.getByRole('link', { name: 'Access control' })).toHaveCount(0);
  await context.close();
});

test('UI-LITE-E-002: all canonical and legacy access routes redirect without access API calls', async ({ page }) => {
  const requests: string[] = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/') && /access-control|access-management|bundle-management|media-access/i.test(url.pathname)) requests.push(request.url());
  });
  for (const route of ['/admin/access-control', '/admin/access-control/bundles/new', '/admin/access-control/policies/new', '/admin/access-management', '/admin/bundle-management', '/admin/media-access']) {
    await page.goto(route);
    await expect(page).toHaveURL(/\/admin(?:\/storage)?$/);
  }
  expect(requests).toEqual([]);
});

test('UI-LITE-E-003: shared admin pages remain accessible to local Admin', async ({ page }) => {
  for (const route of ['/admin/storage', '/admin/import', '/admin/metadata', '/admin/schedules', '/admin/workers', '/admin/stats', '/admin/backups']) {
    await page.goto(route);
    await expect(page.getByRole('heading', { name: /administration/i })).toBeVisible();
    await expect(page.getByText(/requires Owner|permission denied|not authorized/i)).toHaveCount(0);
  }
});

test('UI-LITE-E-004: SQLite snapshot and verification are available with Lite recovery guidance', async ({ page }) => {
  await page.goto('/admin/backups');
  await expect(page.getByText(/Full SQLite snapshots cover application data/i)).toBeVisible();
  const name = `e2e-${Date.now()}`;
  await page.getByLabel('Backup name (optional)').fill(name);
  const backupResponse = page.waitForResponse(response => response.url().includes('/api/global/backups') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Run backup now' }).click();
  const created = await (await backupResponse).json();
  const backup = page.locator('article').filter({ hasText: created.label }).last();
  await expect(backup).toBeVisible({ timeout: 120_000 });
  await backup.getByRole('button', { name: 'Verify' }).click();
  const verification = page.locator('article').filter({ hasText: 'quick verify' }).first();
  await expect(verification.getByText('completed', { exact: true })).toBeVisible({ timeout: 120_000 });
  await expect(page.getByText(/Shut down the Lite server/i)).toBeVisible();
  await expect(page.locator('option[value="diff"]')).toHaveCount(0);
  await expect(page.getByText(/Open the restore console/i)).toHaveCount(0);
});

test('UI-LITE-E-005: unavailable local session reports an error without external login navigation', async ({ page }) => {
  const fault = await controlPost(page, '/session/fault', { status: 503 });
  expect(fault.ok()).toBeTruthy();
  const navigations: string[] = [];
  page.on('request', request => { if (/authentik|\/auth\/login/.test(request.url())) navigations.push(request.url()); });
  await page.goto('/profile');
  await expect(page.getByText(/Unable to load the FrostStream session/i)).toBeVisible();
  expect(navigations).toEqual([]);
  const recovered = await controlPost(page, '/session/recover');
  expect(recovered.ok()).toBeTruthy();
  await page.goto('/profile');
  await expect(page.getByRole('link', { name: 'Open profile for Admin' })).toBeVisible();
});

test('UI-LITE-E-006: workflow survives a Lite process restart', async ({ page }) => {
  const playlistName = `restart-${Date.now()}`;
  await createPlaylist(page, playlistName);
  await expect(page.getByRole('link', { name: 'Open profile for Admin' })).toBeVisible();
  const response = await controlPost(page, '/restart');
  expect(response.ok()).toBeTruthy();
  await page.goto('/profile/playlists');
  await expect(page.getByText(playlistName, { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open profile for Admin' })).toBeVisible();
});

test('UI-LITE-E-007: restored offline snapshot is reflected in the browser', async ({ page }) => {
  const before = `before-restore-${Date.now()}`;
  const after = `after-restore-${Date.now()}`;
  await createPlaylist(page, before);
  await page.goto('/admin/backups');
  await page.getByLabel('Backup name (optional)').fill(`restore-${Date.now()}`);
  await page.getByRole('button', { name: 'Run backup now' }).click();
  await expect(page.locator('article').filter({ hasText: 'full backup' }).first()).toBeVisible({ timeout: 120_000 });
  await expect.poll(async () => {
    const result = await page.request.get('/api/global/backups');
    const data = await result.json();
    return data.backups?.some((backup: { label: string }) => backup.label.endsWith('.sqlite')) ?? false;
  }, { timeout: 120_000 }).toBe(true);
  await createPlaylist(page, after);
  const response = await controlPost(page, '/restore');
  expect(response.ok()).toBeTruthy();
  await page.goto('/profile/playlists');
  await expect(page.getByText(before, { exact: true })).toBeVisible();
  await expect(page.getByText(after, { exact: true })).toHaveCount(0);
  const capabilities = await page.request.get('/api/system/capabilities');
  expect((await capabilities.json()).deploymentMode).toBe('Lite');
});

test('UI-LITE-E-008: Lucene rebuild status and recovered search are visible', async ({ page }) => {
  const query = 'lite-recovery-e2e-no-match';
  await page.goto(`/search?q=${query}`);
  await expect(page.getByRole('heading', { name: `Results for "${query}"` })).toBeVisible();
  await expect(page.getByText('0 results', { exact: true })).toBeVisible();
  const response = await controlPost(page, '/lucene/rebuild');
  expect(response.ok()).toBeTruthy();
  await page.goto(`/search?q=${query}`);
  await expect.poll(async () => {
    const statusResponse = await page.request.get('/api/search/status');
    if (!statusResponse.ok()) return 'unavailable';
    return String((await statusResponse.json()).state).toLowerCase();
  }, { timeout: 120_000 }).toBe('ready');
  const searchResponse = await page.request.get(`/api/search?q=${query}`);
  expect(searchResponse.ok()).toBeTruthy();
  expect(await searchResponse.json()).toMatchObject({ items: [], totalCount: 0 });
  await expect(page.getByText('0 results', { exact: true })).toBeVisible();
});
