<script lang="ts">
  import { VideoPlayer } from 'svelte-video-player';

  let { src, poster = null }: { src: string; poster?: string | null } = $props();
</script>

<VideoPlayer source={src} poster={poster ?? undefined} />
