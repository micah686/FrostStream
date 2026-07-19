<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { Spinner } from 'flowbite-svelte';

  const PREVIOUS_TRACK_ICON =
    '<svg class="media-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 5v14m12-13-8 6 8 6V6Z"/></svg>';
  const NEXT_TRACK_ICON =
    '<svg class="media-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M18 5v14M6 6l8 6-8 6V6Z"/></svg>';
  const DOWNLOAD_ICON =
    '<svg class="media-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 3v12m0 0 4-4m-4 4-4-4M5 21h14"/></svg>';

  let {
    src,
    downloadFileName,
    autoplay = false,
    onPreviousTrack,
    onNextTrack,
    onEnded
  }: {
    src: string;
    downloadFileName: string;
    autoplay?: boolean;
    onPreviousTrack: () => void;
    onNextTrack: () => void;
    onEnded?: () => void;
  } = $props();

  let ready = $state(false);
  let skinElement = $state<HTMLElement | null>(null);

  onMount(() => {
    void initializePlayer();
  });

  async function initializePlayer() {
    await Promise.all([import('@videojs/html/audio/player'), import('@videojs/html/audio/skin')]);
    ready = true;
    await tick();
    addPlaylistControls();
  }

  function addPlaylistControls() {
    const root = skinElement?.shadowRoot;
    const playButton = root?.querySelector('media-play-button');
    const playButtonWrapper = playButton?.closest('.media-button--play__wrapper');
    const muteButton = root?.querySelector('media-mute-button');
    if (!root || !playButtonWrapper || !muteButton) return;

    addResponsiveControlStyles(root);

    const previousControl = createButtonControl(
      'Previous track',
      'Play the previous track',
      PREVIOUS_TRACK_ICON,
      () => onPreviousTrack()
    );
    playButtonWrapper.insertAdjacentElement('beforebegin', previousControl.control);
    previousControl.control.insertAdjacentElement('afterend', previousControl.tooltip);

    const downloadControl = createDownloadControl();
    muteButton.insertAdjacentElement('beforebegin', downloadControl.control);
    downloadControl.control.insertAdjacentElement('afterend', downloadControl.tooltip);

    const nextControl = createButtonControl(
      'Next track',
      'Play the next track',
      NEXT_TRACK_ICON,
      () => onNextTrack()
    );
    muteButton.insertAdjacentElement('afterend', nextControl.control);
    nextControl.control.insertAdjacentElement('afterend', nextControl.tooltip);
  }

  function addResponsiveControlStyles(root: ShadowRoot) {
    const style = document.createElement('style');
    style.textContent = `
      @container media-controls (width < 34rem) {
        .media-button--seek,
        .media-button--playback-rate,
        #seek-backward-tooltip,
        #seek-forward-tooltip,
        #playback-rate-menu {
          display: none;
        }

        .media-time-controls {
          gap: 0.375rem;
          padding-inline: 0.25rem;
        }

        .media-time[type='remaining'] {
          display: none;
        }
      }

      @container media-controls (width < 23rem) {
        .media-time {
          display: none;
        }
      }
    `;
    root.append(style);
  }

  function createButtonControl(
    label: string,
    title: string,
    icon: string,
    onClick: () => void
  ): { control: HTMLButtonElement; tooltip: HTMLElement } {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'media-button media-button--subtle media-button--icon';
    button.setAttribute('aria-label', label);
    button.title = title;
    button.innerHTML = icon;
    button.addEventListener('click', (event) => {
      event.stopPropagation();
      onClick();
    });
    return attachTooltip(button, label);
  }

  function createDownloadControl(): { control: HTMLAnchorElement; tooltip: HTMLElement } {
    const link = document.createElement('a');
    link.className = 'media-button media-button--subtle media-button--icon';
    link.setAttribute('aria-label', 'Download track');
    link.title = 'Download this track as Opus audio';
    link.href = src;
    link.download = downloadFileName;
    link.innerHTML = DOWNLOAD_ICON;
    link.addEventListener('click', (event) => event.stopPropagation());
    return attachTooltip(link, 'Download track');
  }

  function attachTooltip<T extends HTMLElement>(
    control: T,
    label: string
  ): { control: T; tooltip: HTMLElement } {
    const tooltipId = `${label.toLowerCase().replaceAll(' ', '-')}-tooltip`;
    control.setAttribute('commandfor', tooltipId);
    const tooltip = document.createElement('media-tooltip');
    tooltip.id = tooltipId;
    tooltip.setAttribute('side', 'top');
    tooltip.setAttribute('aria-hidden', 'true');
    tooltip.className = 'media-surface media-tooltip';
    tooltip.innerHTML = `<media-tooltip-label>${label}</media-tooltip-label>`;
    return { control, tooltip };
  }
</script>

{#if ready}
  <audio-player class="block w-full">
    <audio-skin
      bind:this={skinElement}
      class="block w-full"
      style="--media-border-radius: 0.5rem; --media-color-primary: oklch(92.9% 0.013 255.508);"
    >
      <!-- svelte-ignore a11y_media_has_caption -->
      <audio {src} preload="metadata" {autoplay} onended={() => onEnded?.()}></audio>
    </audio-skin>
  </audio-player>
{:else}
  <div class="grid min-h-14 w-full place-items-center">
    <Spinner size="6" />
  </div>
{/if}
