<script module lang="ts">
  export interface TextTrackSource {
    src: string;
    srclang: string;
    label: string;
    kind?: 'subtitles' | 'captions';
  }
</script>

<script lang="ts">
  import { onMount } from 'svelte';
  import { Spinner } from 'flowbite-svelte';

  let {
    src,
    poster = null,
    tracks = [],
    startTime = null,
    loop = false,
    autoplay = false,
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
    onProgress?: (positionSeconds: number, durationSeconds: number | null) => void;
    onEnded?: () => void;
  } = $props();

  // The custom elements touch browser globals at import time, so registration is client-only.
  let ready = $state(false);
  let startTimeApplied = false;

  onMount(async () => {
    await Promise.all([
      import('@videojs/html/video/player'),
      import('@videojs/html/video/skin')
    ]);
    ready = true;
  });

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
    <video-skin class="block h-full w-full">
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
