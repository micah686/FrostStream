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
    tracks = []
  }: { src: string; poster?: string | null; tracks?: TextTrackSource[] } = $props();

  // The custom elements touch browser globals at import time, so registration is client-only.
  let ready = $state(false);

  onMount(async () => {
    await Promise.all([
      import('@videojs/html/video/player'),
      import('@videojs/html/video/skin')
    ]);
    ready = true;
  });
</script>

{#if ready}
  <video-player class="block h-full w-full">
    <video-skin class="block h-full w-full">
      <!-- svelte-ignore a11y_media_has_caption — tracks are only present when captions were archived -->
      <video {src} poster={poster ?? undefined} playsinline preload="metadata" class="h-full w-full">
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
