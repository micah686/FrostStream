<script lang="ts">
  import { ClockOutline } from 'flowbite-svelte-icons';
</script>

<svelte:head>
  <title>Background · Jobs · FrostStream</title>
</svelte:head>

<div aria-labelledby="background-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="background-title" class="text-lg font-semibold tracking-tight text-white">Background</h2>
      <p class="mt-1 text-sm text-slate-500">Scheduled and maintenance tasks</p>
    </div>
  </div>

  <div class="mt-6 rounded-xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
    <ClockOutline class="mx-auto h-10 w-10 text-slate-700" />
    <p class="mt-4 text-sm font-semibold text-slate-300">Background jobs are coming soon</p>
    <p class="mt-1 text-sm text-slate-500">
      Scheduled scans, cleanup, and other server tasks will appear here.
    </p>
  </div>
</div>
