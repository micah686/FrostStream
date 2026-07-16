<script module lang="ts">
  export interface TextTrackSource {
    src: string;
    srclang: string;
    label: string;
    kind?: 'subtitles' | 'captions';
    /** ASS/SSA tracks stay in the native Video.js captions menu but render through JASSUB. */
    renderer?: 'native' | 'jassub';
    sourceUrl?: string | null;
  }
</script>

<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { Spinner } from 'flowbite-svelte';
  import type JASSUB from 'jassub';

  let {
    src,
    poster = null,
    tracks = [],
    startTime = null,
    loop = false,
    autoplay = false,
    repeatEnabled = false,
    shuffleEnabled = false,
    onToggleRepeat = undefined,
    onToggleShuffle = undefined,
    onProgress = undefined,
    onEnded = undefined
  }: {
    src: string;
    poster?: string | null;
    tracks?: TextTrackSource[];
    /** Initial playback position in seconds, applied once when metadata loads. */
    startTime?: number | null;
    /** Replay the same video when it ends (repeat mode). */
    loop?: boolean;
    /** Start playback as soon as the media can play. */
    autoplay?: boolean;
    /** Current state of the watch page's repeat mode. */
    repeatEnabled?: boolean;
    /** Current state of the watch page's shuffle mode. */
    shuffleEnabled?: boolean;
    /** Toggles the watch page's repeat mode. */
    onToggleRepeat?: () => void;
    /** Toggles the watch page's shuffle mode. */
    onToggleShuffle?: () => void;
    onProgress?: (positionSeconds: number, durationSeconds: number | null) => void;
    onEnded?: () => void;
  } = $props();

  // The custom elements touch browser globals at import time, so registration is client-only.
  let ready = $state(false);
  let startTimeApplied = false;
  let skinElement = $state<HTMLElement | null>(null);
  let videoElement = $state<HTMLVideoElement | null>(null);
  let repeatButton: HTMLButtonElement | null = null;
  let shuffleButton: HTMLButtonElement | null = null;
  let assRenderer: JASSUB | null = null;
  let assRendererTrackUrl: string | null = null;
  let captionTracksChanged: (() => void) | null = null;

  onMount(() => {
    void initializePlayer();
    return () => {
      captionTracksChanged?.();
      void destroyAssRenderer();
    };
  });

  async function initializePlayer() {
    await Promise.all([import('@videojs/html/video/player'), import('@videojs/html/video/skin')]);
    ready = true;
    await tick();
    addPlaybackModeControls();
    bindCaptionMenu();
  }

  function bindCaptionMenu() {
    const video = videoElement;
    if (!video) return;
    const onChange = () => void syncAssRenderer();
    video.textTracks.addEventListener('change', onChange);
    captionTracksChanged = () => video.textTracks.removeEventListener('change', onChange);
    void syncAssRenderer();
  }

  async function destroyAssRenderer() {
    const renderer = assRenderer;
    assRenderer = null;
    assRendererTrackUrl = null;
    if (renderer) await renderer.destroy();
  }

  async function syncAssRenderer() {
    const video = videoElement;
    if (!video) return;

    const selected = Array.from(video.querySelectorAll<HTMLTrackElement>('track[data-caption-renderer="jassub"]'))
      .find((track) => track.track.mode === 'showing');
    const sourceUrl = selected?.src ?? null;
    if (sourceUrl === assRendererTrackUrl) return;

    await destroyAssRenderer();
    if (!sourceUrl) return;

    try {
      const { default: JASSUBRenderer } = await import('jassub');
      // Keep the browser's selected text track "showing" so Video.js's existing captions menu
      // continues to display the selected language. JASSUB draws the ASS/SSA source above it.
      const renderer = new JASSUBRenderer({ video, subUrl: sourceUrl });
      await renderer.ready;
      if (!selected || videoElement !== video || selected.track.mode !== 'showing' || selected.src !== sourceUrl) {
        await renderer.destroy();
        return;
      }
      assRenderer = renderer;
      assRendererTrackUrl = sourceUrl;
    } catch {
      // Unsupported browsers retain the normal captions-menu selection; the renderer simply
      // remains unavailable instead of interrupting video playback.
    }
  }

  $effect(() => {
    tracks;
    if (ready) void tick().then(syncAssRenderer);
  });

  function addPlaybackModeControls() {
    const controls = skinElement?.shadowRoot?.querySelector('.media-button-group');
    const playButton = controls?.querySelector('media-play-button');
    if (!controls || !playButton || repeatButton || shuffleButton) return;

    repeatButton = createPlaybackModeButton(
      'Repeat',
      'Repeat - keep replaying this video',
      '<svg class="media-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" d="M17 1.5 20.5 5 17 8.5M3.5 5h17v5M7 22.5 3.5 19 7 15.5M20.5 19h-17v-5"/></svg>',
      () => onToggleRepeat?.()
    );
    shuffleButton = createPlaybackModeButton(
      'Shuffle',
      'Shuffle - autoplay picks a random video instead of the next one',
      '<svg class="media-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" d="M16 3h5v5M4 20 9.5 14.5M4 4l13 13M21 16v5h-5M14.5 9.5 17 7"/></svg>',
      () => onToggleShuffle?.()
    );

    // The packaged skin places the backward seek control directly after play/pause.
    playButton.insertAdjacentElement('afterend', shuffleButton);
    playButton.insertAdjacentElement('afterend', repeatButton);
  }

  function createPlaybackModeButton(
    label: string,
    title: string,
    icon: string,
    onClick: () => void
  ): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'media-button media-button--subtle media-button--icon';
    button.setAttribute('aria-label', label);
    button.title = title;
    button.innerHTML = icon;
    button.addEventListener('click', onClick);
    return button;
  }

  $effect(() => {
    updatePlaybackModeButton(repeatButton, repeatEnabled);
    updatePlaybackModeButton(shuffleButton, shuffleEnabled);
  });

  function updatePlaybackModeButton(button: HTMLButtonElement | null, active: boolean) {
    if (!button) return;
    button.setAttribute('aria-pressed', String(active));
    button.style.color = active ? 'var(--media-color-primary, oklch(72% 0.16 250))' : '';
    button.style.backgroundColor = active ? 'color-mix(in oklch, currentColor 16%, transparent)' : '';
  }

  function videoDuration(video: HTMLVideoElement): number | null {
    return Number.isFinite(video.duration) && video.duration > 0 ? video.duration : null;
  }

  function applyStartTime(event: Event) {
    const video = event.currentTarget as HTMLVideoElement;
    if (!startTimeApplied && startTime && startTime > 0) {
      const duration = videoDuration(video);
      if (duration === null || startTime < duration) {
        video.currentTime = startTime;
      }
    }
    startTimeApplied = true;
  }

  function reportProgress(event: Event) {
    const video = event.currentTarget as HTMLVideoElement;
    onProgress?.(video.currentTime, videoDuration(video));
  }
</script>

{#if ready}
  <video-player class="block h-full w-full">
    <video-skin
      bind:this={skinElement}
      class="block h-full w-full"
      style="--media-border-radius: 1rem; --media-video-border-radius: 1rem;"
    >
      <!-- svelte-ignore a11y_media_has_caption — tracks are only present when captions were archived -->
      <video
        bind:this={videoElement}
        {src}
        poster={poster ?? undefined}
        playsinline
        preload="metadata"
        {loop}
        {autoplay}
        class="h-full w-full"
        onloadedmetadata={applyStartTime}
        ontimeupdate={reportProgress}
        onpause={reportProgress}
        onended={() => onEnded?.()}
      >
        {#each tracks as track (track.src)}
          <track
            kind={track.kind ?? 'subtitles'}
            src={track.renderer === 'jassub' ? (track.sourceUrl ?? track.src) : track.src}
            srclang={track.srclang}
            label={track.label}
            data-caption-renderer={track.renderer ?? 'native'}
          />
        {/each}
      </video>
    </video-skin>
  </video-player>
{:else}
  <div class="grid h-full w-full place-items-center">
    <Spinner size="8" />
  </div>
{/if}
