import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/e2e',
  use: { baseURL: 'http://127.0.0.1:25001', browserName: 'chromium' },
  webServer: {
    command: 'pnpm dev --host 127.0.0.1 --port 25001',
    url: 'http://127.0.0.1:25001',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000
  }
});
