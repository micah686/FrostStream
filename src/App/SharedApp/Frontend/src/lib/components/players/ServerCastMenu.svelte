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
  import {
    Cast,
    Pause,
    Play,
    RefreshCw,
    Square,
    Volume2,
    VolumeX
  } from '@lucide/svelte';

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
    storageKey = null,
    version = null,
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
    /** Storage backend to cast from; null casts the default (latest) copy. */
    storageKey?: string | null;
    /** Stored version to cast; null casts the latest. */
    version?: number | null;
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
        startPositionSeconds: fromCurrentPosition && position > 1 ? Math.floor(position) : null,
        storageKey,
        version
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
        'flex items-center gap-1.5 rounded-field border px-4 py-2 text-xs font-semibold transition',
        session
          ? 'border-primary/30 bg-primary/10 text-primary hover:bg-primary/10'
          : 'border-base-300 bg-base-200/70 text-base-content/80 hover:bg-base-300'
      ]}
    >
      <Cast class="h-4 w-4" />
      {session ? `Casting · ${session.deviceName}` : triggerLabel}
    </button>
  {/if}

  {#if open || embedded}
    <div
      class={[
        embedded
          ? 'w-full rounded-box border-[length:var(--border)] border-base-300 bg-base-200/95 p-4 shadow-2xl shadow-black/50 backdrop-blur'
          : 'absolute right-0 z-30 mt-2 w-80 rounded-box border-[length:var(--border)] border-base-300 bg-base-200/95 p-4 shadow-2xl shadow-black/50 backdrop-blur'
      ]}
    >
      {#if error}
        <p class="alert alert-error mb-3 text-xs" role="alert">{error}</p>
      {/if}

      {#if session}
        <!-- Remote control panel -->
        <div class="space-y-3">
          <div>
            <p class="truncate text-sm font-semibold text-base-content">{session.title}</p>
            <p class="text-xs text-base-content/60">
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
            <div class="mt-1 flex justify-between text-[11px] text-base-content/60">
              <span>{formatTime(seeking ? seekValue : displayTime)}</span>
              <span>{formatTime(duration)}</span>
            </div>
          </div>

          <div class="flex items-center justify-center gap-2">
            {#if playing}
              <button type="button" class="cast-ctl" onclick={() => transport(castPause)} title="Pause">
                <Pause class="h-5 w-5" />
              </button>
            {:else}
              <button type="button" class="cast-ctl" onclick={() => transport(castPlay)} title="Play">
                <Play class="h-5 w-5" />
              </button>
            {/if}
            <button type="button" class="cast-ctl" onclick={() => transport(castStop)} title="Stop">
              <Square class="h-5 w-5" />
            </button>
            <button
              type="button"
              class="cast-ctl"
              onclick={() => transport((id) => castVolume(id, { muted: !(session?.snapshot.muted ?? false) }))}
              title={session.snapshot.muted ? 'Unmute' : 'Mute'}
            >
              {#if session.snapshot.muted}
                <VolumeX class="h-5 w-5" />
              {:else}
                <Volume2 class="h-5 w-5" />
              {/if}
            </button>
          </div>

          <div class="flex items-center gap-2">
            <span class="text-[11px] text-base-content/60">Vol</span>
            <div
              class="flex-1"
              onpointerdowncapture={() => (volumeDragging = true)}
              onchangecapture={commitVolume}
            >
              <RangeSlider min={0} max={100} step={1} bind:value={volumeValue} />
            </div>
            <span class="w-8 text-right text-[11px] text-base-content/60">{volumeValue}%</span>
          </div>

          <button
            type="button"
            onclick={disconnect}
            class="btn btn-error btn-sm w-full text-xs"
          >
            Stop casting
          </button>
        </div>
      {:else}
        <!-- Device picker -->
        <div class="mb-3 flex items-center justify-between">
          <p class="text-xs font-semibold tracking-wide text-base-content/60 uppercase">{panelLabel}</p>
          <button
            type="button"
            onclick={() => loadDevices(true)}
            disabled={devicesLoading}
            class="rounded-field p-1 text-base-content/60 transition hover:bg-base-300 hover:text-base-content/90 disabled:opacity-50"
            title="Scan again (takes a few seconds)"
          >
            <RefreshCw class={['h-4 w-4', devicesLoading && 'animate-spin']} />
          </button>
        </div>

        {#if preparingAudio}
          <p class="mb-3 rounded-field border-[length:var(--border)] border-base-content/20 bg-base-200/70 px-3 py-2 text-xs text-base-content/80">
            The audio version is being prepared — try again in a moment.
          </p>
        {/if}

        {#if devicesLoading && devices.length === 0}
          <p class="py-2 text-xs text-base-content/60">Scanning the network…</p>
        {:else if devices.length === 0}
          <p class="py-2 text-xs text-base-content/60">
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
                  class="w-full rounded-field px-3 py-2 text-left text-sm text-base-content/90 transition hover:bg-base-300 disabled:opacity-50"
                >
                  <span class="block truncate font-medium">{device.name}</span>
                  <span class="block truncate text-[11px] text-base-content/50">
                    {device.model ?? 'Cast device'} · {device.host}
                  </span>
                </button>
              </li>
            {/each}
          </ul>
        {/if}

        <div class="space-y-2 border-t border-base-300 pt-3 text-xs text-base-content/80">
          <label class="flex items-center gap-2">
            <input type="checkbox" bind:checked={audioOnly} class="checkbox checkbox-sm checkbox-primary" />
            Audio only
          </label>
          <label class="flex items-center gap-2">
            <input type="checkbox" bind:checked={fromCurrentPosition} class="checkbox checkbox-sm checkbox-primary" />
            Start from current position
          </label>
          {#if captionLanguages.length > 0 && !audioOnly}
            <label class="flex items-center gap-2">
              <span class="shrink-0">Subtitles</span>
              <select
                bind:value={subtitleChoice}
                class="w-full rounded-field border-[length:var(--border)] border-base-content/20 bg-base-200 px-2 py-1 text-xs text-base-content/90"
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
    border: 1px solid color-mix(in srgb, var(--color-base-content) 20%, transparent);
    background: color-mix(in srgb, var(--color-base-200) 70%, transparent);
    color: var(--color-base-content);
    transition: background 150ms;
  }
  .cast-ctl:hover {
    background: var(--color-base-300);
  }
</style>
