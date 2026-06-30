import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';

// Server-side BFF logic is plain Node code, so the tests run in the node environment with a `$lib`
// alias instead of the full SvelteKit plugin (route files only import `@sveltejs/kit`, `$lib`, and
// type-only `./$types`, which esbuild strips).
export default defineConfig({
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts']
  },
  resolve: {
    alias: {
      $lib: fileURLToPath(new URL('./src/lib', import.meta.url))
    }
  }
});
