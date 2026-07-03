<script lang="ts">
  import { onMount } from 'svelte';
  import { VideoPlayer } from 'svelte-video-player';

  let {
    src,
    poster = null,
    startTime = null,
    onProgress = undefined,
    onEnded = undefined
  }: {
    src: string;
    poster?: string | null;
    /** Initial playback position in seconds, applied once when metadata loads. */
    startTime?: number | null;
    onProgress?: (positionSeconds: number, durationSeconds: number | null) => void;
    onEnded?: () => void;
  } = $props();

  let container = $state<HTMLDivElement | null>(null);
  let startTimeApplied = false;

  // svelte-video-player doesn't expose playback events, so hook the underlying <video>.
  onMount(() => {
    const video = container?.querySelector('video');
    if (!video) {
      return;
    }

    const duration = () => (Number.isFinite(video.duration) && video.duration > 0 ? video.duration : null);

    const applyStartTime = () => {
      if (!startTimeApplied && startTime && startTime > 0) {
        const total = duration();
        if (total === null || startTime < total) {
          video.currentTime = startTime;
        }
      }
      startTimeApplied = true;
    };

    const report = () => onProgress?.(video.currentTime, duration());
    const ended = () => onEnded?.();

    if (video.readyState >= HTMLMediaElement.HAVE_METADATA) {
      applyStartTime();
    } else {
      video.addEventListener('loadedmetadata', applyStartTime);
    }
    video.addEventListener('timeupdate', report);
    video.addEventListener('pause', report);
    video.addEventListener('ended', ended);

    return () => {
      video.removeEventListener('loadedmetadata', applyStartTime);
      video.removeEventListener('timeupdate', report);
      video.removeEventListener('pause', report);
      video.removeEventListener('ended', ended);
    };
  });
</script>

<div bind:this={container} class="h-full w-full">
  <VideoPlayer source={src} poster={poster ?? undefined} />
</div>
