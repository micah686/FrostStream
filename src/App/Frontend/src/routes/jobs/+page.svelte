<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Button, Input, Select, Spinner } from 'flowbite-svelte';
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    DownloadOutline,
    ExclamationCircleOutline,
    RefreshOutline,
    ServerOutline
  } from 'flowbite-svelte-icons';
  import { cancelJob, restartJob, setPriority } from '$lib/api/downloadQueue';
  import { createDownloadQueueStore, type DownloadQueueState, type QueueRow } from '$lib/stores/downloadQueue';
  import { formatOptionalBytes, isActive, isCancelled, isDone, isFailed, isQueued } from '$lib/jobs/jobState';
  import { listOptionPresets } from '$lib/api/optionPresets';
  import JobRow from '$lib/components/jobs/JobRow.svelte';

  type FilterKey = 'all' | 'active' | 'queued' | 'failed' | 'done' | 'cancelled';
  type SourceFilterKey = 'all' | 'Direct' | 'Playlist' | 'Channel';

  const queue = createDownloadQueueStore();
  let queueState = $state<DownloadQueueState>({
    rows: [],
    totalCount: 0,
    nextCursor: null,
    connected: false,
    loading: true,
    error: null
  });
  let activeFilter = $state<FilterKey>('all');
  let sourceFilter = $state<SourceFilterKey>('all');
  let query = $state('');
  let pageSize = $state(50);
  let page = $state(1);
  let cursor = $state<string | undefined>(undefined);
  let cursorStack = $state<string[]>([]);
  let actionBusy = $state<Record<string, string>>({});
  let actionError = $state<string | null>(null);
  let now = $state(Date.now());
  let optionPresetsByKey = $state<Map<string, string>>(new Map());
  let searchTimer: number | undefined;

  const unsubscribe = queue.subscribe((value) => {
    queueState = value;
  });

  const pageSizeOptions = [
    { value: 25, name: '25 per page' },
    { value: 50, name: '50 per page' },
    { value: 100, name: '100 per page' },
    { value: 200, name: '200 per page' }
  ];

  const sourceTabs: Array<{ key: SourceFilterKey; label: string }> = [
    { key: 'all', label: 'All sources' },
    { key: 'Direct', label: 'Direct' },
    { key: 'Playlist', label: 'Playlists' },
    { key: 'Channel', label: 'Channels' }
  ];

  const tabs = $derived([
    { key: 'all' as const, label: 'All', count: activeFilter === 'all' ? queueState.totalCount : null },
    { key: 'active' as const, label: 'Active', count: queueState.rows.filter((row) => isActive(row.job.state)).length },
    { key: 'queued' as const, label: 'Queued', count: queueState.rows.filter((row) => isQueued(row.job.state)).length },
    { key: 'failed' as const, label: 'Failed', count: queueState.rows.filter((row) => isFailed(row.job.state)).length },
    { key: 'done' as const, label: 'Done', count: queueState.rows.filter((row) => isDone(row.job.state)).length },
    {
      key: 'cancelled' as const,
      label: 'Cancelled',
      count: queueState.rows.filter((row) => isCancelled(row.job.state)).length
    }
  ]);

  const activeCount = $derived(queueState.rows.filter((row) => isActive(row.job.state)).length);
  const queuedCount = $derived(queueState.rows.filter((row) => isQueued(row.job.state)).length);
  const failedCount = $derived(queueState.rows.filter((row) => isFailed(row.job.state)).length);
  const totalBytes = $derived(
    queueState.rows.reduce((sum, row) => sum + (row.progress?.totalBytes ?? row.job.fileSizeBytes ?? 0), 0)
  );

  onMount(() => {
    queue.connect();
    listOptionPresets()
      .then((presets) => {
        optionPresetsByKey = new Map(presets.map((preset) => [preset.key, preset.name]));
      })
      .catch(() => {});
    const timer = window.setInterval(() => {
      now = Date.now();
    }, 1000);
    // Resync when the tab wakes up: state changes that fired while the SSE
    // connection was dead (e.g. laptop sleep) are live-only and never replayed.
    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        void queue.refresh().catch(() => {});
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
      if (searchTimer) {
        window.clearTimeout(searchTimer);
      }
    };
  });

  onDestroy(() => {
    queue.disconnect();
    unsubscribe();
  });

  async function refreshQueue() {
    actionError = null;
    try {
      await queue.refresh();
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Could not refresh the queue.';
    }
  }

  async function applyQueueParams() {
    await queue.setParams({
      stateGroup: activeFilter,
      sourceKind: sourceFilter === 'all' ? undefined : sourceFilter,
      q: query.trim() || undefined,
      limit: pageSize,
      cursor,
      sort: activeFilter === 'queued' ? 'priority' : 'createdAt'
    });
  }

  function resetPaging(): void {
    page = 1;
    cursor = undefined;
    cursorStack = [];
  }

  async function changeFilter(filter: FilterKey): Promise<void> {
    if (activeFilter === filter) {
      return;
    }
    activeFilter = filter;
    resetPaging();
    await applyQueueParams();
  }

  async function changeSourceFilter(filter: SourceFilterKey): Promise<void> {
    if (sourceFilter === filter) {
      return;
    }
    sourceFilter = filter;
    resetPaging();
    await applyQueueParams();
  }

  function scheduleSearch(): void {
    resetPaging();
    if (searchTimer) {
      window.clearTimeout(searchTimer);
    }
    searchTimer = window.setTimeout(() => {
      void applyQueueParams();
    }, 300);
  }

  async function changePageSize(): Promise<void> {
    pageSize = Number(pageSize);
    resetPaging();
    await applyQueueParams();
  }

  async function nextPage(): Promise<void> {
    if (!queueState.nextCursor) {
      return;
    }
    cursorStack = [...cursorStack, cursor ?? ''];
    cursor = queueState.nextCursor;
    page += 1;
    await applyQueueParams();
  }

  async function previousPage(): Promise<void> {
    if (page <= 1) {
      return;
    }
    const previousStack = cursorStack.slice(0, -1);
    const previousCursor = cursorStack.at(-1);
    cursorStack = previousStack;
    cursor = previousCursor || undefined;
    page = Math.max(1, page - 1);
    await applyQueueParams();
  }

  async function cancelDownload(row: QueueRow) {
    await runAction(row.job.jobId, 'cancel', async () => {
      await cancelJob(row.job.jobId, 'Cancelled from the jobs page.');
    });
  }

  async function restartDownload(row: QueueRow) {
    await runAction(row.job.jobId, 'restart', async () => {
      await restartJob(row.job.jobId);
    });
  }

  async function updatePriority(row: QueueRow) {
    const requested = window.prompt('Set priority from 0 to 100', String(row.job.priority));
    if (requested === null) {
      return;
    }

    const priority = Number(requested.trim());
    if (!Number.isInteger(priority) || priority < 0 || priority > 100) {
      actionError = 'Priority must be a whole number from 0 to 100.';
      return;
    }

    await runAction(row.job.jobId, 'priority', async () => {
      await setPriority(row.job.jobId, priority);
    });
  }

  async function runAction(jobId: string, action: string, fn: () => Promise<void>) {
    actionError = null;
    actionBusy = { ...actionBusy, [jobId]: action };
    try {
      await fn();
      await applyQueueParams();
    } catch (err) {
      actionError = err instanceof Error ? err.message : `Could not ${action} the job.`;
    } finally {
      const { [jobId]: _removed, ...rest } = actionBusy;
      actionBusy = rest;
    }
  }
</script>

<svelte:head>
  <title>Jobs · FrostStream</title>
</svelte:head>

<section aria-labelledby="jobs-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="jobs-title" class="text-2xl font-bold tracking-tight text-white">Jobs</h1>
      <p class="mt-1 text-sm text-slate-500">
        Download queue · page {page} · {queueState.rows.length} shown · {queueState.totalCount} matching
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <span
        class={[
          'inline-flex h-9 items-center gap-2 rounded-full border px-3 text-xs font-semibold',
          queueState.connected
            ? 'border-emerald-500/25 bg-emerald-500/10 text-emerald-300'
            : 'border-slate-700 bg-slate-900 text-slate-400'
        ]}
      >
        <span
          class={[
            'h-2 w-2 rounded-full',
            queueState.connected ? 'bg-emerald-400 shadow-[0_0_10px_rgba(52,211,153,0.8)]' : 'bg-slate-600'
          ]}
        ></span>
        {queueState.connected ? 'SSE live' : 'Connecting'}
      </span>
      <Button
        color="dark"
        onclick={refreshQueue}
        disabled={queueState.loading}
        class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
      >
        {#if queueState.loading}
          <Spinner size="4" class="mr-1.5" />
        {:else}
          <RefreshOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Refresh
      </Button>
      <Button
        href="/download"
        color="blue"
        class="border-0! bg-blue-500! px-3! py-2! text-xs! font-semibold! hover:bg-blue-400!"
      >
        <DownloadOutline class="mr-1.5 h-4 w-4" />
        New
      </Button>
    </div>
  </div>

  <div class="mt-6 grid gap-3 md:grid-cols-4">
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Tracked</p>
      <p class="mt-2 text-2xl font-bold text-white">{queueState.totalCount}</p>
      <p class="mt-1 text-xs text-slate-500">{queueState.rows.length} loaded</p>
    </div>
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Running</p>
      <p class="mt-2 text-2xl font-bold text-white">{activeCount}</p>
      <p class="mt-1 text-xs text-slate-500">active workflow states</p>
    </div>
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Waiting</p>
      <p class="mt-2 text-2xl font-bold text-white">{queuedCount}</p>
      <p class="mt-1 text-xs text-slate-500">queued for a slot</p>
    </div>
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Size</p>
      <p class="mt-2 text-2xl font-bold text-white">{formatOptionalBytes(totalBytes)}</p>
      <p class="mt-1 text-xs text-slate-500">known total bytes</p>
    </div>
  </div>

  <div class="mt-6 flex gap-2 overflow-x-auto pb-1" aria-label="Job source filters">
    {#each sourceTabs as tab}
      <button
        type="button"
        onclick={() => changeSourceFilter(tab.key)}
        class={[
          'inline-flex h-8 shrink-0 items-center rounded-lg border px-3 text-xs font-semibold transition',
          sourceFilter === tab.key
            ? 'border-blue-500/60 bg-blue-500/15 text-blue-200'
            : 'border-slate-800 bg-slate-900/60 text-slate-400 hover:bg-slate-800 hover:text-slate-200'
        ]}
        aria-pressed={sourceFilter === tab.key}
      >
        {tab.label}
      </button>
    {/each}
  </div>

  <div class="mt-3 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
    <div class="flex gap-2 overflow-x-auto pb-1" aria-label="Job filters">
      {#each tabs as tab}
        <button
          type="button"
          onclick={() => changeFilter(tab.key)}
          class={[
            'inline-flex h-9 shrink-0 items-center gap-2 rounded-full px-4 text-xs font-semibold transition',
            activeFilter === tab.key
              ? 'bg-slate-100 text-slate-950'
              : 'bg-slate-800/75 text-slate-300 hover:bg-slate-700'
          ]}
          aria-current={activeFilter === tab.key ? 'page' : undefined}
        >
          {tab.label}
          {#if tab.count !== null}
            <span
              class={[
                'rounded-full px-1.5 py-0.5 text-[10px]',
                activeFilter === tab.key ? 'bg-slate-300 text-slate-800' : 'bg-slate-950/50 text-slate-500'
              ]}
            >
              {tab.count}
            </span>
          {/if}
        </button>
      {/each}
    </div>

    <div class="flex w-full flex-col gap-2 sm:flex-row lg:w-auto">
      <Select
        items={pageSizeOptions}
        bind:value={pageSize}
        onchange={changePageSize}
        aria-label="Jobs per page"
        class="h-10 w-full border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500! sm:w-40"
      />
      <Input
        type="search"
        bind:value={query}
        oninput={scheduleSearch}
        aria-label="Search jobs"
        placeholder="Search source, job id, storage..."
        class="h-10 w-full border-slate-800! bg-slate-900/80! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500! lg:w-80"
      />
    </div>
  </div>

  {#if queueState.error || actionError}
    <div
      class="mt-5 flex items-start gap-3 rounded-xl border border-red-900/60 bg-red-950/35 p-4 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionError ?? queueState.error}</span>
    </div>
  {/if}

  {#if queueState.loading && queueState.rows.length === 0}
    <div class="mt-16 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if queueState.rows.length === 0}
    <div class="mt-8 rounded-xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <ServerOutline class="mx-auto h-10 w-10 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No jobs match this view</p>
      <p class="mt-1 text-sm text-slate-500">Queue a download or change the filters.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each queueState.rows as row (row.job.jobId)}
        <JobRow
          {row}
          {now}
          {optionPresetsByKey}
          busyAction={actionBusy[row.job.jobId]}
          oncancel={cancelDownload}
          onrestart={restartDownload}
          onpriority={updatePriority}
        />
      {/each}
    </div>

    <div class="mt-6 flex flex-col gap-3 border-t border-slate-800/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
      <p class="text-xs text-slate-600">
        Showing {Math.min((page - 1) * pageSize + 1, queueState.totalCount)}-{Math.min(page * pageSize, queueState.totalCount)}
        of {queueState.totalCount}
      </p>
      <div class="flex gap-2">
        <Button
          color="dark"
          disabled={page <= 1 || queueState.loading}
          onclick={previousPage}
          class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
        >
          <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
          Previous
        </Button>
        <Button
          color="dark"
          disabled={!queueState.nextCursor || queueState.loading}
          onclick={nextPage}
          class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
        >
          Next
          <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
        </Button>
      </div>
    </div>
  {/if}
</section>
