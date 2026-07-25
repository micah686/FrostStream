<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    DownloadOutline,
    ExclamationCircleOutline,
    RefreshOutline,
    ServerOutline
  } from 'flowbite-svelte-icons';
  import {
    clearProviderCircuit,
    setPriority,
    startGroup,
    startJob,
    stopGroup,
    stopJob
  } from '$lib/api/downloadQueue';
  import { createDownloadQueueStore, type DownloadQueueState, type QueueRow } from '$lib/stores/downloadQueue';
  import { formatOptionalBytes, isActive, isDone, isFailed, isQueued, isStopped } from '$lib/jobs/jobState';
  import { listOptionPresets } from '$lib/api/optionPresets';
  import JobRow from '$lib/components/jobs/JobRow.svelte';

  type FilterKey = 'all' | 'active' | 'queued' | 'failed' | 'done' | 'stopped';
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
    { key: 'active' as const, label: 'Active', count: queueState.rows.filter((row) => isActive(row.job.status)).length },
    { key: 'queued' as const, label: 'Queued', count: queueState.rows.filter((row) => isQueued(row.job.status)).length },
    { key: 'failed' as const, label: 'Failed', count: queueState.rows.filter((row) => isFailed(row.job.status)).length },
    { key: 'done' as const, label: 'Done', count: queueState.rows.filter((row) => isDone(row.job.status)).length },
    {
      key: 'stopped' as const,
      label: 'Stopped',
      count: queueState.rows.filter((row) => isStopped(row.job.status)).length
    }
  ]);

  const activeCount = $derived(queueState.rows.filter((row) => isActive(row.job.status)).length);
  const queuedCount = $derived(queueState.rows.filter((row) => isQueued(row.job.status)).length);
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

  async function stopDownload(row: QueueRow) {
    await runAction(row.job.jobId, 'stop', async () => {
      await stopJob(row.job.jobId, 'Stopped from the jobs page.');
    });
  }

  async function startDownload(row: QueueRow) {
    await runAction(row.job.jobId, 'start', async () => {
      await startJob(row.job.jobId);
    });
  }

  async function clearDownloadProvider(row: QueueRow) {
    await runAction(row.job.jobId, 'clear-provider', async () => {
      await clearProviderCircuit(providerCircuitName(row));
    });
  }

  function providerCircuitName(row: QueueRow): string {
    const recorded = row.job.failureMessage?.match(/provider circuit for '([^']+)'/i)?.[1];
    if (recorded) return recorded;
    try {
      const host = new URL(row.job.sourceUrl).hostname.toLowerCase().replace(/^www\./, '');
      if (host === 'youtu.be' || host.endsWith('.youtube.com') || host === 'youtube.com') return 'youtube';
      if (host.endsWith('.tiktok.com') || host === 'tiktok.com') return 'tiktok';
      if (host.endsWith('.vimeo.com') || host === 'vimeo.com') return 'vimeo';
      return host.split('.')[0] || host;
    } catch {
      return 'unknown';
    }
  }

  async function stopDownloadGroup(row: QueueRow) {
    await runAction(row.job.jobId, 'group-stop', async () => {
      await stopGroup(row.job.correlationId, 'Stopped from the jobs page.');
    });
  }

  async function startDownloadGroup(row: QueueRow) {
    await runAction(row.job.jobId, 'group-start', async () => {
      await startGroup(row.job.correlationId);
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
  <title>Downloads · Jobs · FrostStream</title>
</svelte:head>

<div aria-labelledby="downloads-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="downloads-title" class="text-lg font-semibold tracking-tight text-base-content">Downloads</h2>
      <p class="mt-1 text-sm text-base-content/50">
        Download queue · page {page} · {queueState.rows.length} shown · {queueState.totalCount} matching
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <span
        class={[
          'inline-flex h-9 items-center gap-2 rounded-full border px-3 text-xs font-semibold',
          queueState.connected
            ? 'border-success/25 bg-success/10 text-success'
            : 'border-base-content/20 bg-base-200 text-base-content/60'
        ]}
      >
        <span
          class={[
            'h-2 w-2 rounded-full',
            queueState.connected ? 'bg-success shadow-[0_0_10px_rgba(52,211,153,0.8)]' : 'bg-base-300'
          ]}
        ></span>
        {queueState.connected ? 'SSE live' : 'Connecting'}
      </span>
      <button class="btn btn-sm btn-neutral text-xs" onclick={refreshQueue} disabled={queueState.loading}>
        {#if queueState.loading}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <RefreshOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Refresh
      </button>
      <a class="btn btn-sm btn-primary text-xs" href="/download">
        <DownloadOutline class="mr-1.5 h-4 w-4" />
        New
      </a>
    </div>
  </div>

  <div class="mt-6 grid gap-3 md:grid-cols-4">
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Tracked</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{queueState.totalCount}</p>
      <p class="mt-1 text-xs text-base-content/50">{queueState.rows.length} loaded</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Running</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{activeCount}</p>
      <p class="mt-1 text-xs text-base-content/50">active workflow states</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Waiting</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{queuedCount}</p>
      <p class="mt-1 text-xs text-base-content/50">not yet dispatched</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Size</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{formatOptionalBytes(totalBytes)}</p>
      <p class="mt-1 text-xs text-base-content/50">known total bytes</p>
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
            ? 'border-primary/60 bg-primary/15 text-primary'
            : 'border-base-300 bg-base-200/60 text-base-content/60 hover:bg-base-300 hover:text-base-content/90'
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
              ? 'bg-base-content text-base-100'
              : 'bg-base-300/75 text-base-content/80 hover:bg-base-300'
          ]}
          aria-current={activeFilter === tab.key ? 'page' : undefined}
        >
          {tab.label}
          {#if tab.count !== null}
            <span
              class={[
                'rounded-full px-1.5 py-0.5 text-[10px]',
                activeFilter === tab.key ? 'bg-base-content/80 text-base-100' : 'bg-base-200/50 text-base-content/50'
              ]}
            >
              {tab.count}
            </span>
          {/if}
        </button>
      {/each}
    </div>

    <div class="flex w-full flex-col gap-2 sm:flex-row lg:w-auto">
      <Select items={pageSizeOptions} bind:value={pageSize} onchange={changePageSize} aria-label="Jobs per page" class="h-10 w-full text-sm sm:w-40" />
      <input class="input w-full h-10 text-sm lg:w-80" type="search" bind:value={query} oninput={scheduleSearch} aria-label="Search jobs" placeholder="Search source, job id, storage..." />
    </div>
  </div>

  {#if queueState.error || actionError}
    <div
      class="mt-5 flex items-start gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionError ?? queueState.error}</span>
    </div>
  {/if}

  {#if queueState.loading && queueState.rows.length === 0}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if queueState.rows.length === 0}
    <div class="mt-8 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <ServerOutline class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No jobs match this view</p>
      <p class="mt-1 text-sm text-base-content/50">Queue a download or change the filters.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each queueState.rows as row (row.job.jobId)}
        <JobRow
          {row}
          {now}
          {optionPresetsByKey}
          busyAction={actionBusy[row.job.jobId]}
          onstop={stopDownload}
          onstart={startDownload}
          onclearprovider={clearDownloadProvider}
          ongroupstop={stopDownloadGroup}
          ongroupstart={startDownloadGroup}
          onpriority={updatePriority}
        />
      {/each}
    </div>

    <div class="mt-6 flex flex-col gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
      <p class="text-xs text-base-content/40">
        Showing {Math.min((page - 1) * pageSize + 1, queueState.totalCount)}-{Math.min(page * pageSize, queueState.totalCount)}
        of {queueState.totalCount}
      </p>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-neutral text-xs" disabled={page <= 1 || queueState.loading} onclick={previousPage}>
          <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
          Previous
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={!queueState.nextCursor || queueState.loading} onclick={nextPage}>
          Next
          <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  {/if}
</div>
