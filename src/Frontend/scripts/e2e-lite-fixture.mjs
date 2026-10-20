import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { createWriteStream, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontendDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(frontendDir, '../..');
const root = resolve(process.env.FROSTSTREAM_E2E_LITE_DATA ?? join(tmpdir(), `froststream-lite-e2e-${process.pid}-${Date.now()}`));
const marker = join(root, '.froststream-e2e-owned');
let hostPort = Number(process.env.FROSTSTREAM_E2E_LITE_HOST_PORT ?? 0);
const controlPort = Number(process.env.FROSTSTREAM_E2E_LITE_CONTROL_PORT ?? 26122);
const frontendPort = Number(process.env.FROSTSTREAM_E2E_LITE_FRONTEND_PORT ?? 26123);
const token = process.env.FROSTSTREAM_E2E_LITE_CONTROL_TOKEN;

if (!token || token.length < 32) throw new Error('The Playwright config must supply a per-run control token.');
if (existsSync(root) && !existsSync(marker)) throw new Error(`Refusing to use non-owned fixture directory: ${root}`);
mkdirSync(root, { recursive: true, mode: 0o700 });
writeFileSync(marker, JSON.stringify({ owner: 'FrostStream Lite E2E', pid: process.pid, root }), { flag: 'wx', mode: 0o600 });
const runDirectory = join(root, 'run');
const logsDirectory = join(root, 'logs');
mkdirSync(runDirectory, { recursive: true });
mkdirSync(logsDirectory, { recursive: true });

let activeRoot = runDirectory;
let sessionFault = false;
let stopping = false;
let host;
let frontend;
const processes = new Set();

function logFile(name) { return createWriteStream(join(logsDirectory, name), { flags: 'a', mode: 0o600 }); }

function start(command, args, name, env = {}, cwd = repo) {
  const output = logFile(`${name}.log`);
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'], detached: process.platform !== 'win32' });
  child.stdout.pipe(output, { end: false });
  child.stderr.pipe(output, { end: false });
  child.once('exit', () => { output.end(); processes.delete(child); });
  processes.add(child);
  return child;
}

function hostEnvironment(dataRoot, port) {
  return {
    ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    Deployment__Mode: 'Lite',
    Persistence__Sqlite__Path: join(dataRoot, 'core.sqlite'),
    Secrets__Local__KeyRingPath: join(dataRoot, 'core.sqlite.keys'),
    Backup__Directory: join(root, 'backups'),
    Search__Lucene__Path: join(dataRoot, 'search'),
    Worker__IncomingRoot: join(dataRoot, 'incoming'),
    FROSTSTREAM_STORAGE_ROOT: join(dataRoot, 'storage'),
    MediaProcessor__TempRoot: join(dataRoot, 'processing'),
    LiveChat__Enabled: 'false', PotProvider__Enabled: 'false', PotBroker__Enabled: 'false'
  };
}

function startHost(dataRoot = activeRoot) {
  host = start('dotnet', ['src/Lite/Backend/bin/Debug/net10.0/Lite.dll'], 'lite-host', hostEnvironment(dataRoot, hostPort));
  return host;
}

async function chooseHostPort() {
  const probe = createServer();
  probe.listen(0, '127.0.0.1');
  await once(probe, 'listening');
  const address = probe.address();
  if (!address || typeof address === 'string') throw new Error('Could not select a Lite host port.');
  probe.close();
  await once(probe, 'close');
  return address.port;
}

async function buildLite() {
  const build = start('dotnet', ['build', 'src/Lite/Backend/Lite.csproj', '--no-restore', '--nologo', '-m:1'], 'lite-build');
  const [code] = await once(build, 'exit');
  if (code !== 0) throw new Error(`Lite build failed with exit code ${code}; inspect ${logsDirectory}/lite-build.log`);
}

async function stopProcess(child) {
  if (!child || child.exitCode !== null) return;
  const signal = name => {
    try { if (process.platform !== 'win32' && child.pid) process.kill(-child.pid, name); else child.kill(name); }
    catch (error) { if (error.code !== 'ESRCH') throw error; }
  };
  signal('SIGTERM');
  const exited = once(child, 'exit').then(() => true);
  if (await Promise.race([exited, new Promise(resolvePromise => setTimeout(() => resolvePromise(false), 10_000))])) return;
  signal('SIGKILL');
  await exited;
}

async function waitReady(url, child, label) {
  const deadline = Date.now() + 120_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`${label} exited with ${child.exitCode}; inspect ${logsDirectory}`);
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch { /* Host is still starting. */ }
    await new Promise(resolvePromise => setTimeout(resolvePromise, 250));
  }
  throw new Error(`${label} did not become ready at ${url}; inspect ${logsDirectory}`);
}

async function recoverSnapshot() {
  const backupRoot = join(root, 'backups');
  const snapshot = existsSync(backupRoot)
    ? readFileSync(join(backupRoot, 'latest-snapshot.txt'), 'utf8').trim()
    : '';
  if (!snapshot || !existsSync(snapshot) || !snapshot.startsWith(`${backupRoot}/`)) throw new Error('No fixture-owned SQLite snapshot is available.');
  const destination = join(root, `restored-${Date.now()}`);
  const recovered = spawn('dotnet', ['src/Lite/Backend/bin/Debug/net10.0/Lite.dll'], {
    cwd: repo,
    env: {
      ...process.env, ...hostEnvironment(join(root, 'recovery-command'), 0),
      'Recovery__Snapshot': snapshot,
      'Recovery__Destination': destination
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  const log = logFile('recovery.log');
  recovered.stdout.pipe(log, { end: false }); recovered.stderr.pipe(log, { end: false });
  await once(recovered, 'exit');
  log.end();
  if (recovered.exitCode !== 0 || !existsSync(join(destination, 'core.sqlite'))) throw new Error('Offline snapshot restore failed; inspect the owned fixture recovery log.');
  activeRoot = destination;
}

function sendJson(response, status, body) {
  const data = Buffer.from(JSON.stringify(body));
  response.writeHead(status, { 'content-type': 'application/json', 'content-length': data.length, 'cache-control': 'no-store' });
  response.end(data);
}

const control = createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', `http://127.0.0.1:${controlPort}`);
  if (url.pathname.startsWith('/api/')) {
    if (url.pathname === '/api/auth/me' && sessionFault) return sendJson(response, 503, { title: 'Fixture session outage' });
    try {
      const requestBody = ['GET', 'HEAD'].includes(request.method ?? 'GET') ? undefined : Buffer.from(await new Promise((resolvePromise, reject) => {
        const chunks = [];
        request.on('data', chunk => chunks.push(chunk));
        request.on('end', () => resolvePromise(Buffer.concat(chunks)));
        request.on('error', reject);
      }));
      const headers = new Headers(request.headers);
      for (const header of ['host', 'connection', 'transfer-encoding', 'content-length']) headers.delete(header);
      if (requestBody) headers.set('content-length', String(requestBody.length));
      const upstream = await fetch(`http://127.0.0.1:${hostPort}${url.pathname}${url.search}`, {
        method: request.method,
        headers,
        body: requestBody
      });
      const content = Buffer.from(await upstream.arrayBuffer());
      const responseHeaders = Object.fromEntries(upstream.headers);
      delete responseHeaders['content-encoding'];
      delete responseHeaders['content-length'];
      delete responseHeaders['transfer-encoding'];
      responseHeaders['content-length'] = String(content.length);
      response.writeHead(upstream.status, responseHeaders);
      response.end(content);
      if (request.method === 'POST' && url.pathname === '/api/global/backups' && upstream.ok) {
        const payload = JSON.parse(content.toString('utf8'));
        if (typeof payload.label === 'string' && payload.label.endsWith('.sqlite')) {
          writeFileSync(join(root, 'backups', 'latest-snapshot.txt'), join(root, 'backups', payload.label), { mode: 0o600 });
        }
      }
    } catch { sendJson(response, 503, { title: 'Lite fixture is restarting' }); }
    return;
  }

  if (request.headers.authorization !== `Bearer ${token}`) return sendJson(response, 403, { error: 'Invalid fixture owner token.' });
  if (request.method !== 'POST') return sendJson(response, 405, { error: 'POST required.' });
  try {
    if (url.pathname === '/session/fault') sessionFault = true;
    else if (url.pathname === '/session/recover') sessionFault = false;
    else if (url.pathname === '/restart') {
      await stopProcess(host);
      startHost();
      await waitReady(`http://127.0.0.1:${hostPort}/health`, host, 'Lite host');
    } else if (url.pathname === '/restore') {
      await stopProcess(host);
      await recoverSnapshot();
      startHost();
      await waitReady(`http://127.0.0.1:${hostPort}/health`, host, 'restored Lite host');
    } else if (url.pathname === '/lucene/rebuild') {
      await stopProcess(host);
      const searchPath = join(activeRoot, 'search');
      if (existsSync(searchPath)) rmSync(searchPath, { recursive: true, force: true });
      startHost();
      await waitReady(`http://127.0.0.1:${hostPort}/health`, host, 'Lite host with rebuilding Lucene index');
    } else return sendJson(response, 404, { error: 'Unknown fixture operation.' });
    sendJson(response, 200, { ok: true });
  } catch (error) {
    sendJson(response, 500, { error: error instanceof Error ? error.message : 'Fixture operation failed.' });
  }
});

async function cleanup() {
  if (stopping) return;
  stopping = true;
  await stopProcess(frontend);
  await stopProcess(host);
  await new Promise(resolvePromise => control.close(resolvePromise));
  if (existsSync(marker)) rmSync(root, { recursive: true, force: true });
}

control.listen(controlPort, '127.0.0.1');
await once(control, 'listening');
if (hostPort === 0) hostPort = await chooseHostPort();
await buildLite();
startHost();
await waitReady(`http://127.0.0.1:${hostPort}/health`, host, 'Lite host');
const capabilities = await fetch(`http://127.0.0.1:${hostPort}/api/system/capabilities`);
if (!capabilities.ok || (await capabilities.json()).deploymentMode !== 'Lite') throw new Error('Owned host did not advertise Lite capabilities.');
if (!existsSync(join(root, 'backups'))) mkdirSync(join(root, 'backups'), { recursive: true });
const viteEnv = { PORT: String(frontendPort), WEBAPI_UPSTREAM: `http://127.0.0.1:${controlPort}` };
frontend = start('pnpm', ['exec', 'vite', '--host', '127.0.0.1'], 'frontend', viteEnv, frontendDir);
await waitReady(`http://127.0.0.1:${frontendPort}`, frontend, 'Vite frontend');
process.stdout.write(JSON.stringify({ frontendUrl: `http://127.0.0.1:${frontendPort}`, controlUrl: `http://127.0.0.1:${controlPort}` }) + '\n');

process.on('SIGINT', () => void cleanup().finally(() => process.exit(0)));
process.on('SIGTERM', () => void cleanup().finally(() => process.exit(0)));
process.on('exit', () => {
  for (const child of processes) {
    try { if (process.platform !== 'win32' && child.pid) process.kill(-child.pid, 'SIGKILL'); else child.kill('SIGKILL'); }
    catch { /* A child may already have exited. */ }
  }
});
