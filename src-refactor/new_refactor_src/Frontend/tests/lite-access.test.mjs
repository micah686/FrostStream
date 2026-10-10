import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import ts from 'typescript';
import { isHttpError, isRedirect } from '@sveltejs/kit';
import { configureFrontendAccess, hasFrontendPermission, requiresLogin } from '../src/lib/frontendAccess.ts';

// Compile the production modules without introducing a separate frontend test framework.
/** @param {string} path */
async function loadModule(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8');
  const compiled = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } }).outputText
    .replaceAll("'#lib/frontendAccess.js'", JSON.stringify(new URL('../src/lib/frontendAccess.ts', import.meta.url).href))
    .replaceAll("'@sveltejs/kit'", JSON.stringify(import.meta.resolve('@sveltejs/kit')));
  return import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
}
const root = await loadModule('../src/routes/+layout.ts');
const accessControl = await loadModule('../src/routes/admin/access-control/+layout.ts');
const http = await loadModule('../src/lib/api/http.ts');
/** @param {'Full' | 'Lite'} deploymentMode */
const capabilities = (deploymentMode, enabled = true) => ({
  deploymentMode,
  integrations: { search: true, liveChat: true, potProvider: true },
  accessManagement: { enabled },
  backups: { provider: 'test', full: true, differential: true, incremental: true, verification: true, pointInTimeRecovery: true }
});
const profile = { subject: 'single-user-owner', name: 'Admin', groups: ['admins'], initials: 'A' };

/** @param {'Full' | 'Lite'} mode */
async function loadSession(mode, status = 200, singleUser = false, location = '/library?tab=History') {
  /** @type {string[]} */
  const calls = [];
  const data = await root.load({ url: new URL(location, 'http://localhost'), fetch: async (/** @type {string} */ url) => {
    calls.push(url);
    return url === '/api/system/capabilities'
      ? Response.json(capabilities(mode))
      : Response.json({ mode: singleUser ? 'single-user' : 'multi-user', profile, authenticated: true }, { status });
  } });
  assert.deepEqual(calls, ['/api/system/capabilities', '/api/auth/me']);
  return data;
}

test('capabilities select Lite independently of session mode and allow every permission decision', async () => {
  const lite = await loadSession('Lite');
  assert.equal(lite.lite, true);
  assert.equal(lite.accessManagementEnabled, false);
  assert.equal(lite.user.name, 'Admin');
  assert.equal(hasFrontendPermission(false), true);
  assert.equal(requiresLogin(401), false);
  const full = await loadSession('Full');
  assert.equal(full.accessManagementEnabled, true);
  assert.equal(hasFrontendPermission(false), false);
  assert.equal(hasFrontendPermission(true), true);
  assert.equal(requiresLogin(401), true);
  assert.equal((await loadSession('Full', 200, true)).accessManagementEnabled, false);
});

test('Lite session failures remain errors while Full preserves login redirects and return paths', async () => {
  await assert.rejects(() => loadSession('Lite', 401), err => isHttpError(err) && err.status === 401);
  await assert.rejects(() => loadSession('Full', 401), err => isRedirect(err) && err.location === '/auth/login?returnTo=%2Flibrary%3Ftab%3DHistory');
  await assert.rejects(() => root.load({ fetch: async () => new Response('', { status: 503 }) }), err => isHttpError(err) && err.status === 503);
});

test('direct access-management routes redirect only in Lite', async () => {
  await assert.rejects(() => accessControl.load({ parent: async () => ({ lite: true }) }), err => isRedirect(err) && err.location === '/admin');
  await accessControl.load({ parent: async () => ({ lite: false }) });
});

test('API boundary suppresses Lite login and CSRF requests but preserves validation and Full behavior', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  /** @type {{ input: RequestInfo | URL, init: RequestInit | undefined }[]} */
  const calls = [];
  /** @type {(string | URL)[]} */
  const navigations = [];
  let status = 401;
  /** @type {typeof fetch} */
  const transport = async (input, init) => {
    calls.push({ input, init });
    if (input === '/api/auth/csrf') return Response.json({ token: 'csrf-test' });
    return status === 400 ? Response.json({ errors: { name: ['Name is required.'] } }, { status }) : Response.json({}, { status });
  };
  globalThis.window = /** @type {Window & typeof globalThis} */ (/** @type {unknown} */ ({ fetch: transport, location: { href: 'http://localhost/library', origin: 'http://localhost', pathname: '/library', search: '', assign: (/** @type {string | URL} */ url) => navigations.push(url) } }));
  try {
    http.installApiFetch();
    globalThis.fetch = window.fetch;
    configureFrontendAccess(capabilities('Lite'));
    await fetch('/api/test', { method: 'POST' });
    assert.equal(calls.length, 1);
    assert.equal(navigations.length, 0);
    status = 400;
    await assert.rejects(() => http.sendJson('/api/test', 'POST', {}), err => err instanceof Error && 'status' in err && err.status === 400 && err.message.includes('Name is required.'));
    await http.logout();
    assert.equal(calls.length, 2);
    configureFrontendAccess(capabilities('Full'));
    status = 200;
    await fetch('/api/test', { method: 'POST' });
    assert.equal(calls[2].input, '/api/auth/csrf');
    assert.equal(new Headers(calls[3].init?.headers).get('X-CSRF-TOKEN'), 'csrf-test');
    status = 401;
    await fetch('/api/test');
    await fetch('/api/test');
    assert.deepEqual(navigations, ['/auth/login?returnTo=%2Flibrary']);
  } finally {
    globalThis.window = previousWindow;
    globalThis.fetch = previousFetch;
  }
});


test('Lite navigation retains Admin across library, playback, search and administration', async () => {
  for (const route of ['/library', '/watch/11111111-1111-1111-1111-111111111111', '/search?q=fixture',
    '/admin', '/admin/import', '/admin/storage', '/admin/workers']) {
    const session = await loadSession('Lite', 200, true, route);
    assert.equal(session.user.subject, profile.subject);
    assert.equal(session.user.name, 'Admin');
    assert.equal(session.lite, true);
    assert.equal(hasFrontendPermission(false), true);
  }
  const full = await loadSession('Full', 200, false, '/admin/storage');
  assert.equal(full.accessManagementEnabled, true);
  assert.equal(hasFrontendPermission(false), false);
});
