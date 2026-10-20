import { defineConfig, devices } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:net';

async function freePort(): Promise<number> {
  const server = createServer();
  server.listen(0, '127.0.0.1');
  await new Promise<void>((resolve, reject) => { server.once('listening', resolve); server.once('error', reject); });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Could not choose a loopback E2E port.');
  server.close();
  await new Promise<void>((resolve, reject) => { server.once('close', resolve); server.once('error', reject); });
  return address.port;
}

const controlPort = process.env.FROSTSTREAM_E2E_LITE_CONTROL_PORT ?? String(await freePort());
const frontendPort = process.env.FROSTSTREAM_E2E_LITE_FRONTEND_PORT ?? String(await freePort());
const baseURL = process.env.FROSTSTREAM_E2E_LITE_URL ?? `http://127.0.0.1:${frontendPort}`;
const controlURL = process.env.FROSTSTREAM_E2E_LITE_CONTROL_URL ?? `http://127.0.0.1:${controlPort}`;
const localFixture = !process.env.FROSTSTREAM_E2E_LITE_URL;
process.env.FROSTSTREAM_E2E_LITE_CONTROL_URL = controlURL;
process.env.FROSTSTREAM_E2E_LITE_FRONTEND_PORT = frontendPort;
process.env.FROSTSTREAM_E2E_LITE_CONTROL_TOKEN ??= randomBytes(32).toString('hex');

export default defineConfig({
  testDir: './tests/e2e/lite',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  reporter: [['list'], ['html', { outputFolder: `${process.env.FROSTSTREAM_E2E_ARTIFACTS ?? '.artifacts/lite-e2e'}/report`, open: 'never' }]],
  outputDir: `${process.env.FROSTSTREAM_E2E_ARTIFACTS ?? '.artifacts/lite-e2e'}/raw`,
  use: {
    ...devices['Desktop Chrome'],
    baseURL,
    launchOptions: process.env.FROSTSTREAM_E2E_CHROMIUM_PATH
      ? { executablePath: process.env.FROSTSTREAM_E2E_CHROMIUM_PATH }
      : undefined,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure'
  },
  webServer: localFixture ? {
    command: 'node scripts/e2e-lite-fixture.mjs',
    url: baseURL,
    reuseExistingServer: false,
    timeout: 180_000,
    env: {
      FROSTSTREAM_E2E_LITE_CONTROL_TOKEN: process.env.FROSTSTREAM_E2E_LITE_CONTROL_TOKEN,
      FROSTSTREAM_E2E_LITE_CONTROL_PORT: controlPort,
      FROSTSTREAM_E2E_LITE_FRONTEND_PORT: frontendPort,
      FROSTSTREAM_E2E_LITE_HOST_PORT: process.env.FROSTSTREAM_E2E_LITE_HOST_PORT ?? '0',
      ...(process.env.FROSTSTREAM_E2E_LITE_DATA ? { FROSTSTREAM_E2E_LITE_DATA: process.env.FROSTSTREAM_E2E_LITE_DATA } : {})
    }
  } : undefined,
  projects: [{ name: 'lite-chromium', use: { ...devices['Desktop Chrome'] } }]
});
