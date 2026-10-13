#!/usr/bin/env node
// Real OIDC sessions and private playlists through packaged Full/NATS/PostgreSQL/OpenFGA.
// Dependencies are installed separately; this does not change the frontend's packages.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: {
  'playwright-module': { type: 'string' }, 'env-file': { type: 'string' },
  output: { type: 'string' }, origin: { type: 'string', default: 'http://localhost:25000' },
  'identity-origin': { type: 'string', default: 'http://localhost:25100' },
  'media-url': { type: 'string' }, 'media-file': { type: 'string' },
  'import-media-file': { type: 'string' },
}});
for (const key of ['playwright-module', 'env-file', 'output']) assert(values[key], `Missing --${key}`);
for (const key of ['origin', 'identity-origin']) {
  assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(values[key]).hostname), 'Use a local disposable installation');
}
const { chromium, request } = await import(pathToFileURL(resolve(values['playwright-module'])).href);
const env = Object.fromEntries(readFileSync(values['env-file'], 'utf8').split('\n')
  .filter(line => /^[A-Z][A-Z0-9_]*=/.test(line)).map(line => {
    const i = line.indexOf('=');
    return [line.slice(0, i), line.slice(i + 1).replace(/^(['"])(.*)\1$/, '$2')];
  }));
assert(env.AUTHENTIK_BOOTSTRAP_TOKEN, 'Missing fixture bootstrap token');
const report = { scope: 'Real Full browser OIDC, session revocation and private playlist isolation' + (values['media-url'] ? '; distributed media acquisition/import/original/range/HLS checks' : ''), passed: false, checks: [], cleanup: [] };
const browser = await chromium.launch({ headless: true });
const admin = await request.newContext({ baseURL: values['identity-origin'], extraHTTPHeaders: { Authorization: `Bearer ${env.AUTHENTIK_BOOTSTRAP_TOKEN}` } });
const users = [], playlists = [], actors = {};
const record = name => { report.checks.push(name); console.log(`PASS: ${name}`); };
async function expectStatus(response, expected, label) {
  assert.equal(response.status(), expected, `${label}: HTTP ${response.status()}, expected ${expected}`);
  return response;
}
async function login(username, password) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(`${values.origin}/auth/login?returnTo=%2Fprofile`);
  const usernameField = page.locator('input[name=uidField]:visible');
  await usernameField.fill(username); await usernameField.press('Enter');
  const passwordField = page.locator('input[type=password]:visible');
  await passwordField.fill(password); await passwordField.press('Enter');
  const isReturned = url => url.origin === values.origin && !url.pathname.startsWith('/auth/');
  await Promise.race([
    page.waitForURL(isReturned),
    page.getByRole('button', { name: 'Continue', exact: true }).waitFor({ state: 'visible' }),
  ]);
  if (!isReturned(new URL(page.url()))) await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.waitForURL(isReturned);
  const me = await (await expectStatus(await context.request.get(`${values.origin}/api/auth/me`), 200, 'session')).json();
  assert.equal(me.mode, 'multi-user'); assert.equal(me.authenticated, true); assert.equal(me.profile.username, username);
  await page.reload();
  await expectStatus(await context.request.get(`${values.origin}/api/auth/me`), 200, 'session after reload');
  const csrf = await (await expectStatus(await context.request.get(`${values.origin}/api/auth/csrf`), 200, 'CSRF')).json();
  return { context, headers: { 'X-CSRF-TOKEN': csrf.token } };
}
async function createPlaylist(actor, name) {
  const response = await actor.context.request.post(`${values.origin}/api/user/playlists`, { headers: actor.headers, data: { name } });
  const playlist = await (await expectStatus(response, 201, 'create private playlist')).json();
  assert(playlist.playlistId); playlists.push({ actor, id: playlist.playlistId });
  return playlist.playlistId;
}
try {
  const anonymous = await request.newContext();
  try { await expectStatus(await anonymous.get(`${values.origin}/api/auth/me`), 401, 'anonymous session'); }
  finally { await anonymous.dispose(); }
  record('Anonymous Full session returns 401');
  const groups = await (await expectStatus(await admin.get('/api/v3/core/groups/?name=admins'), 200, 'fixture group')).json();
  const group = groups.results.find(item => item.name === 'admins'); assert(group, 'Missing blueprint admins group');
  const suffix = randomBytes(6).toString('hex');
  for (const label of ['alice', 'bob']) {
    const username = `phase0-${label}-${suffix}`, password = randomBytes(32).toString('hex');
    const user = await (await expectStatus(await admin.post('/api/v3/core/users/', { data: {
      username, name: `Phase 0 ${label}`, is_active: true, groups: [group.pk], path: 'users/phase0', type: 'internal',
    } }), 201, 'create fixture identity')).json();
    users.push(user.pk);
    await expectStatus(await admin.post(`/api/v3/core/users/${user.pk}/set_password/`, { data: { password } }), 204, 'set fixture password');
    const actor = await login(username, password);
    actors[label] = actor;
    record(`${label}: real Authentik OIDC login and session after reload`);
  }
  const { alice, bob } = actors;
  if (values['media-url']) {
    assert(values['media-file'], '--media-file is required with --media-url');
    const { verifyMedia } = await import('./check-full-media.mjs');
    report.media = await verifyMedia(alice, values.origin, values['media-url'], values['media-file'], values['import-media-file']);
    if (report.media.passed) record('Distributed Full download, import, original/range and HLS playback');
  }
  const aliceId = await createPlaylist(alice, `Phase 0 Alice ${suffix}`);
  const bobId = await createPlaylist(bob, `Phase 0 Bob ${suffix}`);
  for (const [actor, otherId] of [[alice, bobId], [bob, aliceId]]) {
    const list = await (await expectStatus(await actor.context.request.get(`${values.origin}/api/user/playlists`), 200, 'private list')).json();
    assert(Array.isArray(list)); assert(!list.some(item => item.playlistId === otherId));
    await expectStatus(await actor.context.request.get(`${values.origin}/api/user/playlists/${otherId}`), 404, 'foreign private playlist');
    await expectStatus(await actor.context.request.patch(`${values.origin}/api/user/playlists/${otherId}`, { headers: actor.headers, data: { name: 'unauthorized change' } }), 404, 'foreign playlist mutation');
  }
  record('Private playlists are isolated in lists, direct reads and mutations in both directions, including Admin identities');
  for (const item of playlists) await expectStatus(await item.actor.context.request.delete(`${values.origin}/api/user/playlists/${item.id}`, { headers: item.actor.headers }), 204, 'fixture playlist cleanup');
  playlists.length = 0;
  const cookie = (await alice.context.cookies()).find(item => item.name === 'fs_session'); assert(cookie);
  await expectStatus(await alice.context.request.post(`${values.origin}/auth/logout`), 403, 'logout without CSRF');
  const logout = await alice.context.request.post(`${values.origin}/auth/logout`, { headers: alice.headers });
  assert([200, 204].includes(logout.status()), `Logout failed: ${logout.status()}`);
  await expectStatus(await alice.context.request.get(`${values.origin}/api/auth/me`), 401, 'session after logout');
  const replay = await request.newContext({ extraHTTPHeaders: { Cookie: `fs_session=${cookie.value}` } });
  try { await expectStatus(await replay.get(`${values.origin}/api/auth/me`), 401, 'revoked cookie replay'); }
  finally { await replay.dispose(); }
  record('CSRF enforced; logout revokes session and rejects replay of the old cookie');
  report.passed = report.media?.passed ?? true;
  if (!report.passed) process.exitCode = 1;
} catch (error) {
  // Avoid Playwright call logs, URLs with authorization codes, tokens and passwords.
  report.error = error instanceof assert.AssertionError ? error.message : `Browser/API operation failed (${error.name})`;
  console.error(report.error); process.exitCode = 1;
} finally {
  for (const item of playlists) {
    try { const r = await item.actor.context.request.delete(`${values.origin}/api/user/playlists/${item.id}`, { headers: item.actor.headers }); report.cleanup.push({ resource: 'playlist', status: r.status() }); }
    catch { report.cleanup.push({ resource: 'playlist', status: 'failed' }); }
  }
  for (const id of users) {
    try { const r = await admin.delete(`/api/v3/core/users/${id}/`); report.cleanup.push({ resource: 'identity', status: r.status() }); }
    catch { report.cleanup.push({ resource: 'identity', status: 'failed' }); }
  }
  if (report.cleanup.some(item => item.status !== 204)) {
    report.passed = false;
    report.error ??= 'Fixture cleanup did not complete successfully';
    process.exitCode = 1;
  }
  await admin.dispose(); await browser.close();
  mkdirSync(dirname(values.output), { recursive: true });
  writeFileSync(values.output, JSON.stringify(report, null, 2) + '\n');
}
