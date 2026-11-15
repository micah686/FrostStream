<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { ChevronLeft, ChevronRight, Clock, RefreshCw } from '@lucide/svelte';
  import BackgroundRunRow from '#lib/components/jobs/BackgroundRunRow.svelte';
  import { createBackgroundJobsStore, type BackgroundJobsState } from '#lib/stores/backgroundJobs.js';

  const jobs = createBackgroundJobsStore();

  let jobsState = $state<BackgroundJobsState>({
    runs: [],
    runningCount: 0,
    queuedCount: 0,
    connected: false,
    loading: true,
    error: null
  });
  let now = $state(Date.now());
  let refreshing = $state(false);
  let page = $state(1);
  const pageSize = 50;

  const unsubscribe = jobs.subscribe((value) => {
    jobsState = value;
  });

  const finishedCount = $derived(
    jobsState.runs.length - jobsState.runningCount - jobsState.queuedCount
  );
  const totalPages = $derived(Math.max(1, Math.ceil(jobsState.runs.length / pageSize)));
  const visibleRuns = $derived(jobsState.runs.slice((page - 1) * pageSize, page * pageSize));

  $effect(() => {
    if (page > totalPages) {
      page = totalPages;
    }
  });

  onMount(() => {
    jobs.connect();
    const timer = window.setInterval(() => {
      now = Date.now();
    }, 1000);
    // Resync when the tab wakes up: run events are live-only and never replayed, so a
    // connection that died while the laptop slept would otherwise leave a stale list.
    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        void jobs.refresh().catch(() => {});
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  });

  onDestroy(() => {
    jobs.disconnect();
    unsubscribe();
  });

  async function refresh(): Promise<void> {
    refreshing = true;
    try {
      await jobs.refresh();
    } catch {
      // The store surfaces the error through its state.
    } finally {
      refreshing = false;
    }
  }
</script>

<svelte:head>
  <title>Background · Jobs · FrostStream</title>
</svelte:head>

<section class="overflow-hidden rounded-lg border border-base-300 bg-base-200/40" aria-labelledby="background-title">
  <header class="flex flex-wrap items-center gap-3 px-5 pt-5">
    <h2 id="background-title" class="text-xl font-semibold tracking-tight">Background</h2>
    <span class={['inline-flex items-center gap-1.5 text-xs', jobsState.connected ? 'text-success' : 'text-base-content/50']} role="status">
      <span class={['h-1.5 w-1.5 rounded-full', jobsState.connected ? 'bg-success' : 'bg-base-content/30']}></span>
      {jobsState.connected ? 'Live' : 'Reconnecting'}
    </span>
    <button type="button" class="btn btn-sm btn-primary ml-auto text-xs" disabled={refreshing} onclick={refresh}>
      <RefreshCw class={['h-3.5 w-3.5', refreshing ? 'animate-spin' : '']} />Refresh
    </button>
  </header>

  <div class="p-3.5 sm:p-5">
    <p class="mb-4 flex flex-wrap gap-x-4 gap-y-1 text-xs text-base-content/60">
      <span>{jobsState.runs.length} recent runs</span>
      {#if jobsState.runningCount > 0}<span class="text-primary">{jobsState.runningCount} running</span>{/if}
      {#if jobsState.queuedCount > 0}<span class="text-warning">{jobsState.queuedCount} queued</span>{/if}
      {#if finishedCount > 0}<span>{finishedCount} finished</span>{/if}
    </p>

  {#if jobsState.error}
    <p class="alert alert-error mb-4 text-sm" role="alert">
      {jobsState.error}
    </p>
  {/if}

  {#if jobsState.loading && jobsState.runs.length === 0}
    <div class="py-10 text-center">
      <span class="loading loading-spinner loading-md text-base-content/40"></span>
      <p class="mt-4 text-sm text-base-content/50">Loading background tasks…</p>
    </div>
  {:else if jobsState.runs.length === 0}
    <div class="py-10 text-center">
      <Clock class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No background tasks are running</p>
      <p class="mt-1 text-sm text-base-content/50">
        Every scheduled scan, cleanup, and maintenance task lands here the moment it fires, and stays until
        it finishes. This list is live-only and starts empty after a server restart — see
        <a class="link link-hover text-primary" href="/admin/schedules">Schedules</a> for each task's history.
      </p>
    </div>
  {:else}
    <div class="flex flex-col gap-2">
      {#each visibleRuns as run (run.runId)}
        <BackgroundRunRow {run} {now} />
      {/each}
    </div>

    <div class="mt-4 flex flex-wrap items-center justify-between gap-3">
      <p class="text-xs tabular-nums text-base-content/55">
        Showing {Math.min((page - 1) * pageSize + 1, jobsState.runs.length)}-{Math.min(page * pageSize, jobsState.runs.length)}
        of {jobsState.runs.length}
      </p>
      <div class="flex gap-2">
        <button class="btn btn-sm bg-base-100 text-xs font-normal" disabled={page <= 1} onclick={() => (page = Math.max(1, page - 1))}>
          <ChevronLeft class="h-3.5 w-3.5" />
          Previous
        </button>
        <button class="btn btn-sm bg-base-100 text-xs font-normal" disabled={page >= totalPages} onclick={() => (page = Math.min(totalPages, page + 1))}>
          Next
          <ChevronRight class="h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  {/if}
  </div>
</section>
