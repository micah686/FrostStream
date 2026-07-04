<script lang="ts">
  /**
   * Server-side cast control. Unlike CastButton (browser Google Cast SDK), the WebAPI itself
   * discovers Chromecast devices on *its* network via mDNS and drives them, so this works from
   * any browser without a secure context. Live playback status arrives over the session SSE
   * stream; currentTime is interpolated client-side between receiver pushes.
   */
  import {
    listCastDevices,
    listCastSessions,
    startCastSession,
    castPlay,
    castPause,
    castStop,
    castSeek,
    castVolume,
    endCastSession,
    type CastDevice,
    type CastSession
  } from '$lib/api/cast';
  import { readEventStream } from '$lib/sse/eventStream';
  import RangeSlider from '$lib/components/RangeSlider.svelte';

  interface CaptionLanguage {
    languageCode: string;
    captionType: string;
    name?: string | null;
  }

  let {
    mediaGuid,
    title = null,
    captionLanguages = [],
    position = 0,
    protocolId = 'chromecast',
    triggerLabel = 'Cast (server)',
    panelLabel = 'Cast to',
    emptyMessage = "No cast devices found on the server's network.",
    embedded = false
  }: {
    mediaGuid: string;
    title?: string | null;
    captionLanguages?: CaptionLanguage[];
    /** Current local player position in seconds, used for "cast from here". */
    position?: number;
    protocolId?: string;
    triggerLabel?: string;
    panelLabel?: string;
    emptyMessage?: string;
    embedded?: boolean;
  } = $props();

  let open = $state(false);
  let devices = $state<CastDevice[]>([]);
  let devicesLoading = $state(false);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let preparingAudio = $state(false);

  let session = $state<CastSession | null>(null);
  let streamAbort: AbortController | null = null;

  // Cast options
  let audioOnly = $state(false);
  let subtitleChoice = $state('');
  let fromCurrentPosition = $state(true);

  // Interpolation clock + seek/volume drag state
  let now = $state(Date.now());
  let seeking = $state(false);
  let seekValue = $state(0);
  let volumeValue = $state(100);
  let volumeDragging = $state(false);

  const playing = $derived(session?.snapshot.playerState === 'Playing');
  const duration = $derived(session?.snapshot.durationSeconds ?? 0);
  const displayTime = $derived.by(() => {
    const snapshot = session?.snapshot;
    if (!snapshot) {
      return 0;
    }
    const base = snapshot.currentTime;
    if (snapshot.playerState !== 'Playing') {
      return base;
    }
    const elapsed = (now - new Date(snapshot.updatedAt).getTime()) / 1000;
    return Math.min(base + Math.max(0, elapsed), duration || base + elapsed);
  });

  $effect(() => {
    if (!playing) {
      return;
    }
    const timer = setInterval(() => (now = Date.now()), 1000);
    return () => clearInterval(timer);
  });

  $effect(() => {
    if (session && !seeking) {
      seekValue = displayTime;
    }
  });

  $effect(() => {
    if (session && !volumeDragging && session.snapshot.volumeLevel != null) {
      volumeValue = Math.round(session.snapshot.volumeLevel * 100);
    }
  });

  // Tear the SSE stream down with the component.
  $effect(() => () => streamAbort?.abort());

  $effect(() => {
    if (embedded) {
      open = true;
      void loadDevices(false);
      void adoptExistingSession();
    }
  });

  async function toggleOpen() {
    open = !open;
    if (!open) {
      return;
    }
    error = null;
    await Promise.all([loadDevices(false), adoptExistingSession()]);
  }

  async function loadDevices(refresh: boolean) {
    devicesLoading = true;
    try {
      devices = (await listCastDevices(refresh)).filter((device) => device.protocol === protocolId);
      error = null;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : 'Device discovery failed.';
    } finally {
      devicesLoading = false;
    }
  }

  /** Re-attach to a session this server already runs (e.g. after a page reload). */
  async function adoptExistingSession() {
    if (session) {
      return;
    }
    try {
      const sessions = await listCastSessions();
      const existing =
        sessions.find((candidate) => candidate.mediaGuid === mediaGuid && candidate.deviceId.startsWith(`${protocolId}:`)) ??
        sessions.find((candidate) => candidate.mediaGuid === mediaGuid) ??
        sessions.find((candidate) => candidate.deviceId.startsWith(`${protocolId}:`)) ??
        sessions[0];
      if (existing) {
        attachSession(existing);
      }
    } catch {
      // No session list — the picker still works.
    }
  }

  async function startCast(device: CastDevice) {
    busy = true;
    error = null;
    preparingAudio = false;
    try {
      const [language, captionType] = subtitleChoice ? subtitleChoice.split('|', 2) : [null, null];
      const result = await startCastSession(device.id, {
        mediaGuid,
        audioOnly,
        subtitleLanguage: language,
        captionType,
        startPositionSeconds: fromCurrentPosition && position > 1 ? Math.floor(position) : null
      });
      if ('preparing' in result) {
        preparingAudio = true;
        return;
      }
      attachSession(result);
    } catch (cause) {
      error = cause instanceof Error ? cause.message : 'Casting failed.';
    } finally {
      busy = false;
    }
  }

  function attachSession(next: CastSession) {
    session = next;
    streamAbort?.abort();
    const abort = new AbortController();
    streamAbort = abort;
    void streamStatus(next.deviceId, abort);
  }

  async function streamStatus(deviceId: string, abort: AbortController) {
    while (!abort.signal.aborted) {
      try {
        await readEventStream(
          `/api/cast/sessions/${encodeURIComponent(deviceId)}/events`,
          {
            onEvent: (event) => {
              const payload = JSON.parse(event.data) as CastSession;
              if (event.event === 'ended') {
                clearSession();
                abort.abort();
                return;
              }
              if (session?.deviceId === payload.deviceId) {
                session = payload;
              }
            }
          },
          abort.signal
        );
      } catch (cause) {
        if (abort.signal.aborted) {
          return;
        }
        // 404 means the session is gone; anything else retries after a pause.
        if (cause instanceof Error && 'status' in cause && (cause as { status: number }).status === 404) {
          clearSession();
          return;
        }
        await new Promise((resolve) => setTimeout(resolve, 3000));
        continue;
      }
      if (!abort.signal.aborted) {
        // Stream ended without an 'ended' frame (e.g. server restart); retry.
        await new Promise((resolve) => setTimeout(resolve, 3000));
      }
    }
  }

  function clearSession() {
    session = null;
    streamAbort?.abort();
    streamAbort = null;
  }

  async function transport(action: (deviceId: string) => Promise<CastSession>) {
    const current = session;
    if (!current) {
      return;
    }
    try {
      session = await action(current.deviceId);
      error = null;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : 'The cast command failed.';
    }
  }

  function commitSeek() {
    seeking = false;
    void transport((deviceId) => castSeek(deviceId, seekValue));
  }

  function commitVolume() {
    volumeDragging = false;
    void transport((deviceId) => castVolume(deviceId, { level: volumeValue / 100 }));
  }

  async function disconnect() {
    const current = session;
    if (!current) {
      return;
    }
    try {
      await endCastSession(current.deviceId);
    } catch {
      // The receiver may already be gone; drop local state either way.
    }
    clearSession();
  }

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) {
      return '0:00';
    }
    const whole = Math.floor(seconds);
    const h = Math.floor(whole / 3600);
    const m = Math.floor((whole % 3600) / 60);
    const s = whole % 60;
    return h > 0
      ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
      : `${m}:${String(s).padStart(2, '0')}`;
  }

  function captionLabel(caption: CaptionLanguage): string {
    const base = caption.name?.trim() || caption.languageCode;
    return caption.captionType === 'automatic_captions' ? `${base} (auto)` : base;
  }
</script>

<div class="relative">
  {#if !embedded}
    <button
      type="button"
      onclick={toggleOpen}
      title="Cast via the server (no browser Cast SDK needed)"
      class={[
        'flex items-center gap-1.5 rounded-lg border px-4 py-2 text-xs font-semibold transition',
        session
          ? 'border-blue-900/60 bg-blue-950/40 text-blue-300 hover:bg-blue-950/60'
          : 'border-slate-800 bg-slate-900/70 text-slate-300 hover:bg-slate-800'
      ]}
    >
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="h-4 w-4" aria-hidden="true">
        <path d="M2 16.1A5 5 0 0 1 5.9 20M2 12.05A9 9 0 0 1 9.95 20M2 8V6a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6" />
        <circle cx="2" cy="20" r="0.5" fill="currentColor" />
      </svg>
      {session ? `Casting · ${session.deviceName}` : triggerLabel}
    </button>
  {/if}

  {#if open || embedded}
    <div
      class={[
        embedded
          ? 'w-full rounded-xl border border-slate-800 bg-slate-950/95 p-4 shadow-2xl shadow-black/50 backdrop-blur'
          : 'absolute right-0 z-30 mt-2 w-80 rounded-xl border border-slate-800 bg-slate-950/95 p-4 shadow-2xl shadow-black/50 backdrop-blur'
      ]}
    >
      {#if error}
        <p class="mb-3 rounded-lg border border-red-900/60 bg-red-950/30 px-3 py-2 text-xs text-red-300">{error}</p>
      {/if}

      {#if session}
        <!-- Remote control panel -->
        <div class="space-y-3">
          <div>
            <p class="truncate text-sm font-semibold text-white">{session.title}</p>
            <p class="text-xs text-slate-400">
              {session.deviceName} · {session.snapshot.playerState}
            </p>
          </div>

          <div>
            <div
              onpointerdowncapture={() => (seeking = true)}
              onchangecapture={commitSeek}
            >
              <RangeSlider min={0} max={Math.max(duration, 1)} step={1} bind:value={seekValue} disabled={!duration} />
            </div>
            <div class="mt-1 flex justify-between text-[11px] text-slate-400">
              <span>{formatTime(seeking ? seekValue : displayTime)}</span>
              <span>{formatTime(duration)}</span>
            </div>
          </div>

          <div class="flex items-center justify-center gap-2">
            {#if playing}
              <button type="button" class="cast-ctl" onclick={() => transport(castPause)} title="Pause">
                <svg viewBox="0 0 24 24" fill="currentColor" class="h-5 w-5"><path d="M6 4h4v16H6zM14 4h4v16h-4z" /></svg>
              </button>
            {:else}
              <button type="button" class="cast-ctl" onclick={() => transport(castPlay)} title="Play">
                <svg viewBox="0 0 24 24" fill="currentColor" class="h-5 w-5"><path d="M8 5v14l11-7z" /></svg>
              </button>
            {/if}
            <button type="button" class="cast-ctl" onclick={() => transport(castStop)} title="Stop">
              <svg viewBox="0 0 24 24" fill="currentColor" class="h-5 w-5"><path d="M6 6h12v12H6z" /></svg>
            </button>
            <button
              type="button"
              class="cast-ctl"
              onclick={() => transport((id) => castVolume(id, { muted: !(session?.snapshot.muted ?? false) }))}
              title={session.snapshot.muted ? 'Unmute' : 'Mute'}
            >
              {#if session.snapshot.muted}
                <svg viewBox="0 0 24 24" fill="currentColor" class="h-5 w-5"><path d="M16.5 12A4.5 4.5 0 0 0 14 8v2.2l2.4 2.4c.06-.2.1-.4.1-.6zM3 4.3 4.3 3 21 19.7 19.7 21l-2.6-2.6A8.9 8.9 0 0 1 14 19.8v-2.1a6.9 6.9 0 0 0 1.6-.8L12 13.3V18l-5-5H3V9h3.3zM12 4 9.9 6.1 12 8.2z" /></svg>
              {:else}
                <svg viewBox="0 0 24 24" fill="currentColor" class="h-5 w-5"><path d="M3 9v6h4l5 5V4L7 9zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z" /></svg>
              {/if}
            </button>
          </div>

          <div class="flex items-center gap-2">
            <span class="text-[11px] text-slate-400">Vol</span>
            <div
              class="flex-1"
              onpointerdowncapture={() => (volumeDragging = true)}
              onchangecapture={commitVolume}
            >
              <RangeSlider min={0} max={100} step={1} bind:value={volumeValue} />
            </div>
            <span class="w-8 text-right text-[11px] text-slate-400">{volumeValue}%</span>
          </div>

          <button
            type="button"
            onclick={disconnect}
            class="w-full rounded-lg border border-red-900/60 bg-red-950/30 px-3 py-2 text-xs font-semibold text-red-300 transition hover:bg-red-950/50"
          >
            Stop casting
          </button>
        </div>
      {:else}
        <!-- Device picker -->
        <div class="mb-3 flex items-center justify-between">
          <p class="text-xs font-semibold tracking-wide text-slate-400 uppercase">{panelLabel}</p>
          <button
            type="button"
            onclick={() => loadDevices(true)}
            disabled={devicesLoading}
            class="rounded-md p-1 text-slate-400 transition hover:bg-slate-800 hover:text-slate-200 disabled:opacity-50"
            title="Scan again (takes a few seconds)"
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              class={['h-4 w-4', devicesLoading && 'animate-spin']}
              aria-hidden="true"
            >
              <path d="M21 12a9 9 0 1 1-2.64-6.36M21 3v6h-6" />
            </svg>
          </button>
        </div>

        {#if preparingAudio}
          <p class="mb-3 rounded-lg border border-slate-700 bg-slate-900/70 px-3 py-2 text-xs text-slate-300">
            The audio version is being prepared — try again in a moment.
          </p>
        {/if}

        {#if devicesLoading && devices.length === 0}
          <p class="py-2 text-xs text-slate-400">Scanning the network…</p>
        {:else if devices.length === 0}
          <p class="py-2 text-xs text-slate-400">
            {emptyMessage}
          </p>
        {:else}
          <ul class="mb-3 space-y-1">
            {#each devices as device (device.id)}
              <li>
                <button
                  type="button"
                  onclick={() => startCast(device)}
                  disabled={busy}
                  class="w-full rounded-lg px-3 py-2 text-left text-sm text-slate-200 transition hover:bg-slate-800 disabled:opacity-50"
                >
                  <span class="block truncate font-medium">{device.name}</span>
                  <span class="block truncate text-[11px] text-slate-500">
                    {device.model ?? 'Cast device'} · {device.host}
                  </span>
                </button>
              </li>
            {/each}
          </ul>
        {/if}

        <div class="space-y-2 border-t border-slate-800 pt-3 text-xs text-slate-300">
          <label class="flex items-center gap-2">
            <input type="checkbox" bind:checked={audioOnly} class="accent-blue-500" />
            Audio only
          </label>
          <label class="flex items-center gap-2">
            <input type="checkbox" bind:checked={fromCurrentPosition} class="accent-blue-500" />
            Start from current position
          </label>
          {#if captionLanguages.length > 0 && !audioOnly}
            <label class="flex items-center gap-2">
              <span class="shrink-0">Subtitles</span>
              <select
                bind:value={subtitleChoice}
                class="w-full rounded-md border border-slate-700 bg-slate-900 px-2 py-1 text-xs text-slate-200"
              >
                <option value="">Off</option>
                {#each captionLanguages as caption (caption.languageCode + caption.captionType)}
                  <option value={`${caption.languageCode}|${caption.captionType}`}>{captionLabel(caption)}</option>
                {/each}
              </select>
            </label>
          {/if}
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .cast-ctl {
    display: grid;
    place-items: center;
    width: 2.25rem;
    height: 2.25rem;
    border-radius: 9999px;
    border: 1px solid var(--color-slate-700, #334155);
    background: color-mix(in srgb, var(--color-slate-900, #0f172a) 70%, transparent);
    color: var(--color-slate-200, #e2e8f0);
    transition: background 150ms;
  }
  .cast-ctl:hover {
    background: var(--color-slate-800, #1e293b);
  }
</style>
