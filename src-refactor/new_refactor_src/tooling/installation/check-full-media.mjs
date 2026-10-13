// Invoked with a real OIDC actor by check-authentik.mjs. No substituted handlers.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';

export async function verifyMedia(actor, origin, sourceUrl, fixtureFile, importFixtureFile) {
  const checks = [], failures = [], timings = {};
  const storageKey = 'phase0-' + randomUUID();
  const expected = readFileSync(fixtureFile);
  const importExpected = readFileSync(importFixtureFile ?? fixtureFile);
  const importTitle = 'Phase 0 Full import ' + storageKey;
  const digest = bytes => createHash('sha256').update(bytes).digest('hex');
  async function call(method, path, data) {
    const response = await actor.context.request.fetch(origin + path, { method, data, headers: actor.headers });
    assert(response.ok(), `${method} ${path}: HTTP ${response.status()}: ${(await response.text()).slice(0, 500)}`);
    return response;
  }
  async function json(method, path, data) { return (await call(method, path, data)).json(); }
  async function eventually(action, label, seconds = 300) {
    const deadline = performance.now() + seconds * 1000;
    while (performance.now() < deadline) {
      const result = await action();
      if (result) return result;
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
    throw new Error(`Timed out: ${label}`);
  }
  await json('POST', '/api/storage/local/create', { key: storageKey, protocol: 'Local', path: '/data/phase0-media', description: 'Disposable distributed media verification' });
  let start = performance.now();
  const job = await json('POST', '/api/downloads/video', { sourceUrl, storageKey, forceDownload: true });
  assert(job.jobId, 'Download did not return a job');
  await eventually(async () => {
    // Durable submission is accepted before DataBridge has persisted the first event.
    const response = await actor.context.request.get(`${origin}/api/downloads/queue/${job.jobId}`);
    if (response.status() === 404) return false;
    assert(response.ok(), `Queue poll: HTTP ${response.status()}`);
    const state = await response.json();
    assert(!['Failed', 'Stopped'].includes(state.status), `Download ${state.status}: ${JSON.stringify(state)}`);
    return state.status === 'Completed';
  }, 'distributed download');
  timings.downloadMilliseconds = performance.now() - start;
  const media = await json('GET', `/api/downloads/queue/${job.jobId}/media`);
  assert(media.mediaGuid);
  checks.push('Real HTTP acquisition via yt-dlp, durable NATS job, worker processing and DataBridge persistence');

  async function playback(guid, expectedBytes) {
    const started = performance.now();
    const original = await call('GET', `/api/media/watch/${guid}`);
    const bytes = await original.body();
    assert.equal(digest(bytes), digest(expectedBytes), 'Original byte hash differs');
    assert(bytes.length > 0);
    const range = await actor.context.request.get(`${origin}/api/media/watch/${guid}`, { headers: { ...actor.headers, Range: 'bytes=0-15' } });
    assert.equal(range.status(), 206); assert.equal((await range.body()).length, 16);
    timings[`${guid}:originalAndRange`] = { elapsedMilliseconds: performance.now() - started, originalBytes: bytes.length, sha256: digest(bytes) };
    checks.push(`Original SHA-256 and HTTP 206 range passed for ${guid}`);
    const manifestPath = `/api/media/stream/${guid}/index.m3u8`;
    // Request once to start encoding, then inspect status before requesting again:
    // a new GET after failure can enqueue a retry and conceal the failed state.
    await call('GET', manifestPath);
    const master = await eventually(async () => {
      const queue = await json('GET', `/api/media/renditions/queue?q=${guid}&storageKey=${encodeURIComponent(storageKey)}`);
      const failed = queue.items?.find(item => item.status.toLowerCase() === 'failed');
      assert(!failed, `HLS rendition failed: ${failed?.errorMessage}`);
      if (!queue.items?.some(item => item.status.toLowerCase() === 'ready')) return null;
      const response = await call('GET', manifestPath);
      return response.status() === 200 ? response.text() : null;
    }, 'HLS generation');
    async function follow(text, base, depth = 0) {
      assert(depth < 5, 'Unexpected HLS nesting');
      const entries = text.split(/\r?\n/).map(x => x.trim()).filter(x => x && !x.startsWith('#'));
      assert(entries.length > 0, 'Empty HLS playlist');
      let segmentBytes = 0;
      for (const entry of entries) {
        const url = new URL(entry, base);
        assert.equal(url.origin, origin, 'Unexpected external HLS URL');
        const response = await call('GET', url.pathname + url.search);
        if (entry.includes('.m3u8')) segmentBytes += await follow(await response.text(), url.href, depth + 1);
        else segmentBytes += (await response.body()).length;
      }
      return segmentBytes;
    }
    const hlsBytes = await follow(master, origin + manifestPath);
    assert(hlsBytes > 0);
    return { elapsedMilliseconds: performance.now() - started, originalBytes: bytes.length, hlsBytes };
  }
  try {
    timings.downloadPlayback = await playback(media.mediaGuid, expected);
    checks.push('Downloaded original SHA-256, HTTP 206 range, real FFmpeg HLS playlists and segments');
  } catch (error) {
    failures.push({ stage: 'downloadPlayback', message: error.message });
  }
  start = performance.now();
  const session = await json('POST', '/api/global/imports/sessions', { storageKey });
  assert(session.sessionId);
  const base = `/api/global/imports/sessions/${session.sessionId}`;
  await eventually(async () => {
    const state = await json('GET', base);
    assert.notEqual(state.status, 'ScanFailed', state.errorMessage);
    return state.status === 'Reviewing' && state.totalItems === 1;
  }, 'incoming scan');
  const { items } = await json('GET', base + '/items');
  assert.equal(items.length, 1);
  await json('PATCH', `${base}/items/${items[0].itemId}`, { title: importTitle, provider: 'generic', sourceMediaId: storageKey, sourceUrl: 'https://fixture.example.test/' + storageKey });
  await json('POST', base + '/items/bulk', { action: 'Include', itemIds: [items[0].itemId] });
  await eventually(async () => (await json('GET', base)).probedItems === 1, 'FFprobe');
  await json('POST', base + '/commit', {});
  await eventually(async () => {
    const state = await json('GET', base);
    assert.notEqual(state.status, 'CompletedWithFailures', state.errorMessage);
    return state.status === 'Completed' && state.importedItems + state.alreadyImportedItems === 1;
  }, 'import commit');
  timings.importMilliseconds = performance.now() - start;
  checks.push('Distributed incoming scan, metadata edit, FFprobe and commit/finalize');
  const importedGuid = await eventually(async () => {
    const search = await json('GET', '/api/search?q=' + encodeURIComponent(importTitle));
    return search.items.find(hit => hit.media.title === importTitle)?.media.mediaGuid;
  }, 'imported media search');
  try {
    timings.importPlayback = await playback(importedGuid, importExpected);
    checks.push('Imported original SHA-256, HTTP 206 range and HLS playback');
  } catch (error) {
    failures.push({ stage: 'importPlayback', message: error.message });
  }
  return { passed: failures.length === 0, checks, failures, timings, sourceSha256: digest(expected), jobId: job.jobId, sessionId: session.sessionId, mediaGuid: media.mediaGuid, importedMediaGuid: importedGuid };
}
