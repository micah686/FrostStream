<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { ChevronLeft, ChevronRight, Clock, RefreshCw } from '@lucide/svelte';
  import BackgroundRunRow from '$lib/components/jobs/BackgroundRunRow.svelte';
  import { createBackgroundJobsStore, type BackgroundJobsState } from '$lib/stores/backgroundJobs';

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

<div aria-labelledby="background-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="background-title" class="text-lg font-semibold tracking-tight text-base-content">Background</h2>
      <p class="mt-1 text-sm text-base-content/50">
        Every schedule that has gone off, from the moment it fires until it finishes
        {#if jobsState.runningCount > 0}
          · <span class="font-semibold text-primary">{jobsState.runningCount} running</span>
        {/if}
        {#if jobsState.queuedCount > 0}
          · <span class="font-semibold text-warning">{jobsState.queuedCount} queued</span>
        {/if}
        {#if finishedCount > 0}
          · {finishedCount} recently finished
        {/if}
      </p>
    </div>
    <div class="flex items-center gap-2">
      {#if jobsState.connected}
        <span class="badge badge-success h-8 px-3 text-xs font-semibold text-success-content">SSE Live</span>
      {/if}
      <button
        type="button"
        class="btn btn-sm btn-neutral text-xs disabled:opacity-40"
        disabled={refreshing}
        onclick={refresh}
      >
        <RefreshCw class={['h-3.5 w-3.5', refreshing ? 'animate-spin' : '']} />
        Refresh
      </button>
    </div>
  </div>

  {#if jobsState.error}
    <p class="alert alert-error mt-4 text-sm" role="alert">
      {jobsState.error}
    </p>
  {/if}

  {#if jobsState.loading && jobsState.runs.length === 0}
    <div class="mt-6 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <span class="loading loading-spinner loading-md text-base-content/40"></span>
      <p class="mt-4 text-sm text-base-content/50">Loading background tasks…</p>
    </div>
  {:else if jobsState.runs.length === 0}
    <div class="mt-6 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <Clock class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No background tasks are running</p>
      <p class="mt-1 text-sm text-base-content/50">
        Every scheduled scan, cleanup, and maintenance task lands here the moment it fires, and stays until
        it finishes. This list is live-only and starts empty after a server restart — see
        <a class="link link-hover text-primary" href="/admin/schedules">Schedules</a> for each task's history.
      </p>
    </div>
  {:else}
    <div class="mt-6 flex flex-col gap-3">
      {#each visibleRuns as run (run.runId)}
        <BackgroundRunRow {run} {now} />
      {/each}
    </div>

    <div class="mt-6 flex flex-col gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
      <p class="text-xs text-base-content/40">
        Showing {Math.min((page - 1) * pageSize + 1, jobsState.runs.length)}-{Math.min(page * pageSize, jobsState.runs.length)}
        of {jobsState.runs.length}
      </p>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-neutral text-xs" disabled={page <= 1} onclick={() => (page = Math.max(1, page - 1))}>
          <ChevronLeft class="mr-1 h-3.5 w-3.5" />
          Previous
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={page >= totalPages} onclick={() => (page = Math.min(totalPages, page + 1))}>
          Next
          <ChevronRight class="ml-1 h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  {/if}
</div>
