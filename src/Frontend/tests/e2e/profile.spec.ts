import { test, expect } from '@playwright/test';

const config = { id: 1, key: 'archive', name: 'Archive config', description: null, storageKey: 'default', cookieProfileKey: null, workerTag: null, ytDlpOptions: null, ignoreKeywords: [], priority: 10 };
const preset = { id: 1, key: 'quality', name: 'Quality preset', description: null, ytDlpOptions: {}, createdAt: '2025-01-01T00:00:00Z', lastUpdated: null };
const cookie = { profileKey: 'youtube', displayName: 'YouTube cookies', site: 'youtube.com', createdAt: null, lastUpdated: null };
const provider = { providerKey: 'alerts', providerKind: 'discord', enabled: true, displayName: 'Alerts', defaultTo: null, eventKeys: [], notifyConfig: {} };
const playlist = { playlistId: 'list-1', name: 'Favorites', description: 'My videos', itemCount: 0, items: [], userNote: null, createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z' };
const note = { targetType: 'channel', targetId: '42', targetTitle: 'Favorite channel', note: 'Original note', createdAt: null, updatedAt: null };

test.beforeEach(async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    const method = route.request().method();
    let body: unknown = {};
    if (path === '/api/system/capabilities') body = { deploymentMode: 'Full', accessManagement: { enabled: true }, integrations: {}, backups: {} };
    else if (path === '/api/auth/me') body = { mode: 'multi-user', authenticated: true, profile: { subject: 'user-123', name: 'Test User', username: 'tester', email: 'test@example.com', initials: 'TU', groups: ['viewers'] } };
    else if (path === '/api/user/config-sets') body = method === 'GET' ? [config] : config;
    else if (path === '/api/user/config-sets/archive') body = config;
    else if (path === '/api/user/option-presets') body = method === 'GET' ? [preset] : preset;
    else if (path === '/api/user/option-presets/quality') body = preset;
    else if (path === '/api/user/cookies') body = [cookie];
    else if (path.startsWith('/api/user/cookies/')) body = cookie;
    else if (path === '/api/user/notifications/providers') body = [provider];
    else if (path === '/api/user/notifications/providers/alerts') body = method === 'PUT' ? { ...provider, ...route.request().postDataJSON() } : provider;
    else if (path === '/api/user/notes' || path === '/api/user/notes/search') body = { items: [note], page: 1, totalCount: 1, hasMore: false };
    else if (path === '/api/user/notes/channel/42') body = { ...note, ...route.request().postDataJSON() };
    else if (path === '/api/user/playlists') body = method === 'GET' ? [playlist] : playlist;
    else if (path === '/api/user/playlists/list-1') body = method === 'PATCH' ? { ...playlist, ...route.request().postDataJSON() } : playlist;
    else if (path.includes('storage')) body = [];
    if (method === 'DELETE') return route.fulfill({ status: 204 });
    await route.fulfill({ json: body });
  });
});

test('profile overview displays account details and navigation', async ({ page }) => {
  await page.goto('/profile');
  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
  await expect(page.getByText('test@example.com')).toBeVisible();
  await expect(page.getByText('user-123')).toBeVisible();
  await expect(page.getByText('viewers')).toBeVisible();
  const sections = page.getByLabel('Profile sections');
  await expect(sections.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page');
  await sections.getByRole('link', { name: 'Appearance' }).click();
  await expect(sections.getByRole('link', { name: 'Appearance' })).toHaveAttribute('aria-current', 'page');
});

test('appearance and custom CSS persist in this browser', async ({ page }) => {
  await page.goto('/profile/appearance');
  await page.locator('input[name=theme][value=dark]').check();
  await page.reload();
  await expect(page.locator('input[name=theme][value=dark]')).toBeChecked();
  await page.getByRole('link', { name: 'Edit', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Custom CSS' })).toBeVisible();
  await page.getByRole('tab', { name: 'CSS', exact: true }).click();
  await page.getByLabel('CSS', { exact: true }).fill(':root { --color-primary: red; }');
  await page.getByRole('button', { name: 'Apply CSS' }).click();
  await expect(page.getByText('Saved', { exact: true })).toBeVisible();
  await page.reload();
  await page.getByRole('tab', { name: 'CSS', exact: true }).click();
  await expect(page.getByLabel('CSS', { exact: true })).toHaveValue(':root { --color-primary: red; }');
});

test('config sets list, edit and create', async ({ page }) => {
  await page.goto('/profile/config-sets');
  await expect(page.getByRole('heading', { name: 'Archive config' })).toBeVisible();
  await page.getByRole('link', { name: 'Edit config set Archive config' }).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Archive config');
  await expect(page.getByLabel('Key', { exact: true })).toBeDisabled();
  await page.goto('/profile/config-sets/new');
  await page.getByLabel('Key', { exact: true }).fill('new-config');
  await page.getByLabel('Name', { exact: true }).fill('New config');
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/config-sets' && r.method() === 'POST');
  await page.locator('form').getByRole('button', { name: /Create/ }).click();
  expect((await request).postDataJSON()).toMatchObject({ key: 'new-config', name: 'New config' });
  await expect(page).toHaveURL(/\/profile\/config-sets$/);
});

test('option presets list, edit and create', async ({ page }) => {
  await page.goto('/profile/option-presets');
  await page.getByRole('link', { name: 'Edit option preset Quality preset' }).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Quality preset');
  await expect(page.getByLabel('Key', { exact: true })).toBeDisabled();
  await page.goto('/profile/option-presets/new');
  await page.getByLabel('Key', { exact: true }).fill('new-preset');
  await page.getByLabel('Name', { exact: true }).fill('New preset');
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/option-presets' && r.method() === 'POST');
  await page.locator('form').getByRole('button', { name: /Create/ }).click();
  expect((await request).postDataJSON()).toMatchObject({ key: 'new-preset', name: 'New preset' });
  await expect(page).toHaveURL(/\/profile\/option-presets$/);
});

test('cookie profiles list, replace and create', async ({ page }) => {
  await page.goto('/profile/cookie-management');
  await expect(page.getByText('YouTube cookies')).toBeVisible();
  await page.getByRole('link', { name: 'Replace cookies for youtube' }).click();
  await expect(page.getByRole('heading', { name: 'Replace cookie profile' })).toBeVisible();
  await expect(page.getByLabel('Site (optional)')).toHaveValue('youtube.com');
  await page.goto('/profile/cookie-management/new');
  await page.getByLabel('Key', { exact: true }).fill('new-cookie');
  await page.getByLabel('Display name (optional)').fill('New cookies');
  await page.locator('#cookie-content').fill('# Netscape HTTP Cookie File');
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/cookies/new-cookie' && r.method() === 'PUT');
  await page.getByRole('button', { name: /Save|Create/ }).click();
  expect((await request).postDataJSON()).toMatchObject({ displayName: 'New cookies', content: '# Netscape HTTP Cookie File' });
  await expect(page).toHaveURL(/\/profile\/cookie-management$/);
});

test('notifications can toggle a provider and open its editor', async ({ page }) => {
  await page.goto('/profile/notifications');
  await expect(page.getByRole('heading', { name: 'Notifications', exact: true })).toBeVisible();
  const toggle = page.getByRole('checkbox', { name: 'Disable Alerts' });
  await expect(toggle).toBeChecked();
  const request = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/notifications/providers/alerts' && r.method() === 'PUT');
  await toggle.uncheck();
  expect((await request).postDataJSON()).toMatchObject({ enabled: false });
  await expect(page.getByRole('checkbox', { name: 'Enable Alerts' })).not.toBeChecked();
  await page.getByRole('link', { name: 'Edit notification provider alerts' }).click();
  await expect(page.getByLabel('Provider key')).toHaveValue('alerts');
  await expect(page.getByLabel('Provider key')).toBeDisabled();
  await expect(page.getByLabel('Display name')).toHaveValue('Alerts');
  await expect(page.getByLabel('Profile sections').getByRole('link', { name: 'Notifications' })).toHaveAttribute('aria-current', 'page');
  await page.goto('/profile/notification-providers/new');
  await expect(page.getByLabel('Provider key')).toBeEditable();
  await expect(page.getByRole('heading', { name: /New notification provider/ })).toBeVisible();
});

test('notes can be searched and edited', async ({ page }) => {
  await page.goto('/profile/notes');
  await expect(page.getByText('Original note')).toBeVisible();
  await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveAttribute('href', '/channel/42');
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.locator('article textarea').fill('Updated note');
  const save = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/notes/channel/42' && r.method() === 'PUT');
  await page.getByRole('button', { name: 'Save changes' }).click();
  expect((await save).postDataJSON()).toEqual({ note: 'Updated note' });
  await expect(page.getByText('Updated note')).toBeVisible();
  await page.getByLabel('Search notes').fill('favorite');
  const search = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/notes/search');
  await page.locator('form').getByRole('button', { name: 'Search' }).click();
  expect(new URL((await search).url()).searchParams.get('q')).toBe('favorite');
});

test('playlists list, create and edit details', async ({ page }) => {
  await page.goto('/profile/playlists');
  await expect(page.getByRole('button', { name: 'Open playlist Favorites' })).toBeVisible();
  await page.getByRole('link', { name: 'Edit playlist Favorites' }).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Favorites');
  await page.getByLabel('Name', { exact: true }).fill('Renamed playlist');
  const update = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/playlists/list-1' && r.method() === 'PATCH');
  await page.getByRole('button', { name: 'Save changes' }).click();
  expect((await update).postDataJSON()).toMatchObject({ name: 'Renamed playlist' });
  await expect(page.getByText('Saved', { exact: true })).toBeVisible();
  await page.goto('/profile/playlists/new');
  await expect(page.getByRole('button', { name: 'Create playlist' })).toBeDisabled();
  await page.getByLabel('Name', { exact: true }).fill('New playlist');
  const create = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/playlists' && r.method() === 'POST');
  await page.getByRole('button', { name: 'Create playlist' }).click();
  expect((await create).postDataJSON()).toMatchObject({ name: 'New playlist' });
  await expect(page).toHaveURL(/\/profile\/playlists\/list-1$/);
});

test('deletion requires confirmation and removes a config set', async ({ page }) => {
  await page.goto('/profile/config-sets');
  await page.getByRole('button', { name: 'Delete config set Archive config' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Archive config');
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('heading', { name: 'Archive config' })).toBeVisible();
  await page.getByRole('button', { name: 'Delete config set Archive config' }).click();
  const deletion = page.waitForRequest(r => new URL(r.url()).pathname === '/api/user/config-sets/archive' && r.method() === 'DELETE');
  await dialog.getByRole('button', { name: 'Delete config set' }).click();
  await deletion;
  await expect(page.getByText('No config sets yet')).toBeVisible();
});

test('shows a profile section API error', async ({ page }) => {
  await page.route(url => url.pathname === '/api/user/option-presets', route => route.fulfill({ status: 503, body: 'Presets unavailable' }));
  await page.goto('/profile/option-presets');
  await expect(page.getByRole('alert')).toContainText('Presets unavailable');
});
