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
  import type JASSUB from 'jassub';

  const REPEAT_ICON =
    '<svg class="media-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="m16 10 3-3m0 0-3-3m3 3H5v3m3 4-3 3m0 0 3 3m-3-3h14v-3" /></svg>';
  const SHUFFLE_ICON =
    '<svg class="media-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.484 9.166 15 7h5m0 0-3-3m3 3-3 3M4 17h4l1.577-2.253M4 7h4l7 10h5m0 0-3 3m3-3-3-3" /></svg>';
  const FOCUS_ICON =
    '<svg class="media-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M7 21h10"/><rect width="20" height="14" x="2" y="3" rx="2" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2"/></svg>';

  let {
    src,
    poster = null,
    tracks = [],
    startTime = null,
    loop = false,
    autoplay = false,
    repeatEnabled = false,
    shuffleEnabled = false,
    focusAvailable = false,
    focusActive = false,
    onToggleRepeat = undefined,
    onToggleShuffle = undefined,
    onToggleFocus = undefined,
    onFocusToFullscreen = undefined,
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
    /** Shows the media-and-chat focus control when this archive has a chat replay. */
    focusAvailable?: boolean;
    /** Whether the watch page is currently in media-and-chat focus mode. */
    focusActive?: boolean;
    /** Toggles the watch page's repeat mode. */
    onToggleRepeat?: () => void;
    /** Toggles the watch page's shuffle mode. */
    onToggleShuffle?: () => void;
    onToggleFocus?: () => void;
    /** Releases a parent-owned focus fullscreen before this player claims normal fullscreen. */
    onFocusToFullscreen?: () => Promise<void>;
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
  let focusButton: HTMLButtonElement | null = null;
  let fullscreenButton: HTMLElement | null = null;
  let assRenderer: JASSUB | null = null;
  let assRendererTrackUrl: string | null = null;
  let captionTracksChanged: (() => void) | null = null;
  let fullscreenClickCleanup: (() => void) | null = null;
  let completingFocusFullscreenHandoff = false;

  onMount(() => {
    void initializePlayer();
    return () => {
      stopProgressLoop();
      captionTracksChanged?.();
      fullscreenClickCleanup?.();
      void destroyAssRenderer();
    };
  });

  // `timeupdate` only fires ~4 times a second, so consumers that render against the playhead (the
  // live chat replay) would advance in ~250ms clumps. While playing, report every frame instead.
  let progressFrame: number | null = null;

  function startProgressLoop() {
    if (progressFrame !== null) {
      return;
    }
    const step = () => {
      const video = videoElement;
      if (!video || video.paused || video.ended) {
        progressFrame = null;
        return;
      }
      onProgress?.(video.currentTime, videoDuration(video));
      progressFrame = requestAnimationFrame(step);
    };
    progressFrame = requestAnimationFrame(step);
  }

  function stopProgressLoop() {
    if (progressFrame !== null) {
      cancelAnimationFrame(progressFrame);
      progressFrame = null;
    }
  }

  async function initializePlayer() {
    await Promise.all([import('@videojs/html/video/player'), import('@videojs/html/video/skin')]);
    ready = true;
    await tick();
    addPlaybackModeControls();
    addFocusModeControl();
    interceptFocusFullscreenHandoff();
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
      const renderer = new JASSUBRenderer({
        video,
        subUrl: sourceUrl,
        // Fetch a missing subtitle font from Google Fonts when it is not embedded or installed.
        queryFonts: 'localandremote'
      });
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

    const repeatControl = createPlaybackModeButton(
      'Repeat',
      'Repeat - keep replaying this video',
      REPEAT_ICON,
      'R',
      () => onToggleRepeat?.()
    );
    const shuffleControl = createPlaybackModeButton(
      'Shuffle',
      'Shuffle - autoplay picks a random video instead of the next one',
      SHUFFLE_ICON,
      'S',
      () => onToggleShuffle?.()
    );

    // The packaged skin places the backward seek control directly after play/pause.
    playButton.insertAdjacentElement('afterend', shuffleControl.button);
    shuffleControl.button.insertAdjacentElement('afterend', shuffleControl.tooltip);
    shuffleControl.tooltip.setAttribute('aria-hidden', 'true');
    playButton.insertAdjacentElement('afterend', repeatControl.button);
    repeatControl.button.insertAdjacentElement('afterend', repeatControl.tooltip);
    repeatControl.tooltip.setAttribute('aria-hidden', 'true');

    repeatButton = repeatControl.button;
    shuffleButton = shuffleControl.button;
  }

  function addFocusModeControl() {
    // PiP and fullscreen live in the trailing control group. Keeping focus there makes it the
    // second-to-last control: immediately after PiP and immediately before fullscreen.
    const controls = skinElement?.shadowRoot?.querySelector('.media-button-group:last-child');
    if (!controls || focusButton || !focusAvailable) return;

    const focusControl = createPlaybackModeButton(
      'Theater Mode',
      'Theater Mode - show only video and live chat',
      FOCUS_ICON,
      'T',
      () => onToggleFocus?.()
    );
    const pipButton = controls.querySelector('media-pip-button');
    fullscreenButton = controls.querySelector('media-fullscreen-button');
    if (pipButton) {
      pipButton.insertAdjacentElement('afterend', focusControl.button);
      focusControl.button.insertAdjacentElement('afterend', focusControl.tooltip);
    } else if (fullscreenButton) {
      fullscreenButton.insertAdjacentElement('beforebegin', focusControl.button);
      focusControl.button.insertAdjacentElement('afterend', focusControl.tooltip);
    } else {
      controls.append(focusControl.button, focusControl.tooltip);
    }
    focusControl.tooltip.setAttribute('aria-hidden', 'true');
    focusButton = focusControl.button;
  }

  function interceptFocusFullscreenHandoff() {
    const root = skinElement?.shadowRoot;
    if (!root || fullscreenClickCleanup) return;

    const onClick = async (event: Event) => {
      if (!focusActive || completingFocusFullscreenHandoff) return;
      const fullscreenControl = event.composedPath().find(
        (node): node is Element => node instanceof Element && node.matches('media-fullscreen-button')
      );
      if (!fullscreenControl) return;

      // Video.js would otherwise attempt to fullscreen its skin while the focus container owns
      // fullscreen. Release focus first, then replay this click through Video.js so its internal
      // fullscreen state stays aligned with the browser (including its normal exit behavior).
      event.preventDefault();
      event.stopImmediatePropagation();
      await onFocusToFullscreen?.();
      await tick();
      completingFocusFullscreenHandoff = true;
      const videoJsControl = fullscreenControl as HTMLElement & {
        activate?: (state: unknown) => void;
        mediaState?: { value: unknown };
      };
      videoJsControl.activate?.(videoJsControl.mediaState?.value);
      queueMicrotask(() => {
        completingFocusFullscreenHandoff = false;
      });
    };
    root.addEventListener('click', onClick, true);
    fullscreenClickCleanup = () => root.removeEventListener('click', onClick, true);
  }

  function createPlaybackModeButton(
    label: string,
    title: string,
    icon: string,
    shortcut: string,
    onClick: () => void
  ): { button: HTMLButtonElement; tooltip: HTMLElement } {
    const tooltipId = `${label.toLowerCase()}-tooltip`;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'media-button media-button--subtle media-button--icon';
    button.setAttribute('aria-label', label);
    button.title = title;
    button.setAttribute('commandfor', tooltipId);
    button.innerHTML = icon;
    button.addEventListener('click', onClick);
    const tooltip = document.createElement('media-tooltip');
    tooltip.id = tooltipId;
    tooltip.setAttribute('side', 'top');
    tooltip.className = 'media-surface media-tooltip';
    tooltip.innerHTML = `
      <media-tooltip-label>${label}</media-tooltip-label>
      <media-tooltip-shortcut class="media-tooltip__kbd">${shortcut}</media-tooltip-shortcut>
    `;
    return { button, tooltip };
  }

  $effect(() => {
    updatePlaybackModeButton(repeatButton, repeatEnabled);
    updatePlaybackModeButton(shuffleButton, shuffleEnabled);
    updatePlaybackModeButton(focusButton, focusActive);
  });

  $effect(() => {
    if (ready) addFocusModeControl();
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

  export function seekTo(seconds: number, play = true) {
    const video = videoElement;
    if (!video || !Number.isFinite(seconds) || seconds < 0) return;

    const duration = videoDuration(video);
    video.currentTime = duration === null ? seconds : Math.min(seconds, duration);
    onProgress?.(video.currentTime, duration);

    if (play) {
      video.play().catch(() => {
        // If the browser blocks playback, the seek still succeeded.
      });
    }
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
        onplaying={startProgressLoop}
        onseeked={reportProgress}
        onpause={(event) => {
          stopProgressLoop();
          reportProgress(event);
        }}
        onended={() => {
          stopProgressLoop();
          onEnded?.();
        }}
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
    <span class="loading loading-spinner loading-md"></span>
  </div>
{/if}
