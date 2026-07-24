<script lang="ts">
  import { VideoCameraOutline } from 'flowbite-svelte-icons';
</script>

<svelte:head>
  <title>Encoding · Jobs · FrostStream</title>
</svelte:head>

<div aria-labelledby="encoding-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="encoding-title" class="text-lg font-semibold tracking-tight text-white">Encoding</h2>
      <p class="mt-1 text-sm text-slate-500">Transcoding and rendition jobs</p>
    </div>
  </div>

  <div class="mt-6 rounded-xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
    <VideoCameraOutline class="mx-auto h-10 w-10 text-slate-700" />
    <p class="mt-4 text-sm font-semibold text-slate-300">Encoding jobs are coming soon</p>
    <p class="mt-1 text-sm text-slate-500">
      Stream and audio rendition progress will appear here.
    </p>
  </div>
</div>
