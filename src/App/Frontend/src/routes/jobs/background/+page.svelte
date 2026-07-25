<script lang="ts">
  import { Clock } from '@lucide/svelte';
</script>

<svelte:head>
  <title>Background · Jobs · FrostStream</title>
</svelte:head>

<div aria-labelledby="background-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="background-title" class="text-lg font-semibold tracking-tight text-base-content">Background</h2>
      <p class="mt-1 text-sm text-base-content/50">Scheduled and maintenance tasks</p>
    </div>
  </div>

  <div class="mt-6 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
    <Clock class="mx-auto h-10 w-10 text-base-content/30" />
    <p class="mt-4 text-sm font-semibold text-base-content/80">Background jobs are coming soon</p>
    <p class="mt-1 text-sm text-base-content/50">
      Scheduled scans, cleanup, and other server tasks will appear here.
    </p>
  </div>
</div>
