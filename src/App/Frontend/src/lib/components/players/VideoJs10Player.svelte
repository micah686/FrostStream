<script lang="ts">
  import { onMount } from 'svelte';
  import { Spinner } from 'flowbite-svelte';

  let { src, poster = null }: { src: string; poster?: string | null } = $props();

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
      <!-- svelte-ignore a11y_media_has_caption — archived captions are not exposed over HTTP yet -->
      <video {src} poster={poster ?? undefined} playsinline preload="metadata" class="h-full w-full"
      ></video>
    </video-skin>
  </video-player>
{:else}
  <div class="grid h-full w-full place-items-center">
    <Spinner size="8" />
  </div>
{/if}
