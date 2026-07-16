<script module lang="ts">
  export interface TextTrackSource {
    src: string;
    srclang: string;
    label: string;
    kind?: 'subtitles' | 'captions';
  }
</script>

<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { Spinner } from 'flowbite-svelte';

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
  let repeatButton: HTMLButtonElement | null = null;
  let shuffleButton: HTMLButtonElement | null = null;

  onMount(() => {
    void initializePlayer();
  });

  async function initializePlayer() {
    await Promise.all([import('@videojs/html/video/player'), import('@videojs/html/video/skin')]);
    ready = true;
    await tick();
    addPlaybackModeControls();
  }

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
    <video-skin bind:this={skinElement} class="block h-full w-full">
      <!-- svelte-ignore a11y_media_has_caption — tracks are only present when captions were archived -->
      <video
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
          <track kind={track.kind ?? 'subtitles'} src={track.src} srclang={track.srclang} label={track.label} />
        {/each}
      </video>
    </video-skin>
  </video-player>
{:else}
  <div class="grid h-full w-full place-items-center">
    <Spinner size="8" />
  </div>
{/if}
