import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Render the actual shared component with each host's page capabilities.
const server = await createServer({
  configFile: false,
  plugins: [{
    name: 'release-test-page',
    resolveId(id) { if (id === '$app/state') return '\0release-test-page'; },
    load(id) { if (id === '\0release-test-page') return 'export const page = { data: {} };'; }
  }, svelte()],
  resolve: { alias: { '$lib': fileURLToPath(new URL('../src/lib', import.meta.url)) } },
  server: { middlewareMode: true },
  appType: 'custom'
});
try {
  const { page } = await server.ssrLoadModule('$app/state');
  const { default: Backups } = await server.ssrLoadModule('/src/lib/components/admin/BackupsSection.svelte');
  const { render } = await server.ssrLoadModule('svelte/server');
  test('shared backup page presents SQLite recovery and supported operations in Lite', () => {
    page.data = { lite: true, capabilities: { backups: {
      full: true, differential: false, verification: true, deepVerification: false, pointInTimeRecovery: false
    } } };
    const { body } = render(Backups);
    assert.match(body, /complete SQLite snapshot/);
    assert.doesNotMatch(body, /<option[^>]*value="diff"/);
    assert.match(body, /Shut down the Lite server/);
    assert.match(body, /retaining all older keys/);
    assert.doesNotMatch(body, /Open the restore console/);
  });
  test('shared backup page retains differential backups and the recovery console in Full', () => {
    page.data = { lite: false, capabilities: { backups: {
      full: true, differential: true, verification: true, deepVerification: true, pointInTimeRecovery: true
    } } };
    const { body } = render(Backups);
    assert.match(body, /complete cluster backup/);
    assert.match(body, /<option[^>]*value="diff"/);
    assert.match(body, /Open the restore console/);
    assert.doesNotMatch(body, /Shut down the Lite server/);
  });
} finally {
  await server.close();
}
