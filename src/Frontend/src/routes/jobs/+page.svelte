<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Modal, Select } from '#lib/components/ui/index.js';
  import {
    ChevronLeft,
    ChevronRight,
    CircleAlert,
    Folder,
    ExternalLink,
    Search,
    RefreshCw,
    Server,
    Trash2
  } from '@lucide/svelte';
  import {
    fetchQueue,
    fetchCollections,
    cleanupHistory,
    clearProviderCircuit,
    setPriority,
    startGroup,
    startJob,
    stopGroup,
    stopJob
  } from '#lib/api/downloadQueue.js';
  import type { DownloadCollection } from '#lib/api/downloadQueue.js';
  import { createDownloadQueueStore, type DownloadQueueState, type QueueRow } from '#lib/stores/downloadQueue.js';
  import { isActive, isDone, isFailed, isQueued, isStopped } from '#lib/jobs/jobState.js';
  import { listOptionPresets } from '#lib/api/optionPresets.js';
  import JobRow from '#lib/components/jobs/JobRow.svelte';

  type FilterKey = 'active' | 'queued' | 'done' | 'failed' | 'stopped' | 'all';
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
  let view = $state<'jobs' | 'collections'>('jobs');
  let selectedCollection = $state<string | null>(null);
  let collectionRows = $state<QueueRow[]>([]);
  let collectionGroups = $state<DownloadCollection[]>([]);
  let collectionsLoading = $state(false);
  let collectionController: AbortController | undefined;
  let selectedFilters = $state<FilterKey[]>(['all']);
  let statusCounts = $state<Record<string, number>>({});
  const statusOptions = [
    { key: 'active', label: 'Active', matches: isActive, statuses: ['Running', 'Stopping', 'Compensating'] },
    { key: 'queued', label: 'Queued', matches: isQueued, statuses: ['Queued'] },
    { key: 'failed', label: 'Failed', matches: isFailed, statuses: ['Failed'] },
    { key: 'stopped', label: 'Stopped', matches: isStopped, statuses: ['Stopped'] },
    { key: 'done', label: 'Done', matches: isDone, statuses: ['Completed', 'CompletedWithWarnings', 'AlreadyDownloaded', 'Ignored'] }
  ] as const;
  const allSelected = $derived(selectedFilters.includes('all'));
  const selectedStatuses = $derived(statusOptions.filter(option => selectedFilters.includes(option.key)).flatMap(option => [...option.statuses]));
  function matchesStatus(status: string) {
    return allSelected || statusOptions.some(option => selectedFilters.includes(option.key) && option.matches(status));
  }
  let sourceFilter = $state<SourceFilterKey>('all');
  let query = $state('');
  let pageSize = $state(50);
  let page = $state(1);
  let cursor = $state<string | undefined>(undefined);
  let cursorStack = $state<string[]>([]);
  let actionBusy = $state<Record<string, string>>({});
  let actionError = $state<string | null>(null);
  let optionPresetsByKey = $state<Map<string, string>>(new Map());
  let searchTimer: number | undefined;

  let cleanupOpen = $state(false);
  // Empty while the field is blank; the number input's binding replaces it with a number on input.
  let cleanupRetentionDays = $state<number | string | null>('');
  let cleanupIncludeFailed = $state(false);
  let cleanupBusy = $state(false);
  let cleanupError = $state<string | null>(null);
  let cleanupNotice = $state<string | null>(null);

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

  const liveCollectionRows = $derived.by(() => {
    const live = new Map(queueState.rows.map(row => [row.job.jobId, row]));
    return collectionRows.map(row => {
      const latest = live.get(row.job.jobId);
      return latest && Date.parse(latest.job.updatedAt) >= Date.parse(row.job.updatedAt) ? latest : row;
    });
  });
  const scopedRows = $derived(selectedCollection
    ? liveCollectionRows.filter(row => row.job.correlationId === selectedCollection)
    : queueState.rows);
  const collections = $derived.by(() => {
    const groups = new Map<string, QueueRow[]>();
    for (const row of liveCollectionRows) {
      if (!['playlist', 'channel'].includes(row.job.sourceKind.toLowerCase())) continue;
      const group = groups.get(row.job.correlationId) ?? [];
      group.push(row);
      groups.set(row.job.correlationId, group);
    }
    const records = new Map(collectionGroups.map(group => [group.correlationId, group]));
    return [...new Set([...records.keys(), ...groups.keys()])].map(id => {
      const rows = groups.get(id) ?? [];
      const record = records.get(id);
      return { id, rows, kind: rows[0]?.job.sourceKind ?? record?.kind ?? 'Playlist',
      name: rows.find(row => row.job.collectionTitle?.trim())?.job.collectionTitle?.trim()
        || record?.sourceUrl || `Collection ${id}`,
      sourceUrl: record?.sourceUrl ?? rows[0]?.job.collectionSourceUrl ?? rows[0]?.job.sourceUrl,
      createdAt: record?.createdAt ?? rows[0]?.job.createdAt,
      status: record?.status,
      done: rows.filter(row => isDone(row.job.status)).length,
      active: rows.filter(row => isActive(row.job.status)).length,
      queued: rows.filter(row => isQueued(row.job.status)).length,
      failed: rows.filter(row => isFailed(row.job.status)).length
      };
    });
  });
  const selectedGroup = $derived(collections.find(group => group.id === selectedCollection));
  const visibleCollections = $derived(collections.filter(group => `${group.name} ${group.sourceUrl ?? ''} ${group.rows.map(row => row.job.sourceUrl).join(' ')}`.toLowerCase().includes(query.toLowerCase())));
  const holdingCollectionCards = $derived(collectionGroups.filter(group =>
    (group.status === 'Queued' || group.status === 'Expanding')
    && (allSelected || (group.status === 'Queued' ? selectedFilters.includes('queued') : selectedFilters.includes('active')))
    && (sourceFilter === 'all' || group.kind.toLowerCase() === sourceFilter.toLowerCase()
      || (sourceFilter === 'Channel' && group.kind === 'CreatorMonitor'))
    && `${group.sourceUrl} ${group.correlationId}`.toLowerCase().includes(query.toLowerCase())));
  const collectionMatches = $derived(scopedRows.filter(row => {
    return matchesStatus(row.job.status) && (sourceFilter === 'all' || row.job.sourceKind === sourceFilter) && `${row.job.title ?? ''} ${row.job.collectionTitle ?? ''} ${row.job.sourceUrl} ${row.job.jobId}`.toLowerCase().includes(query.toLowerCase());
  }));
  const visibleRows = $derived(selectedCollection ? collectionMatches.slice((page - 1) * pageSize, page * pageSize) : queueState.rows.filter(row => matchesStatus(row.job.status)));
  const matchingCount = $derived(selectedCollection ? collectionMatches.length : selectedFilters.length ? queueState.totalCount : 0);
  const tabs = $derived(statusOptions.map(option => ({ ...option, count: selectedCollection ? scopedRows.filter(row => option.matches(row.job.status)).length : statusCounts[option.key] })));
  const allCount = $derived(selectedCollection ? scopedRows.length : statusCounts.all);
  const hasNext = $derived(selectedCollection ? page * pageSize < matchingCount : Boolean(queueState.nextCursor));

  $effect(() => {
    if (selectedCollection) page = Math.min(page, Math.max(1, Math.ceil(matchingCount / pageSize)));
  });

  async function loadCollections() {
    collectionController?.abort();
    const controller = new AbortController();
    collectionController = controller;
    collectionsLoading = true;
    actionError = null;
    try {
      const rows: QueueRow[] = [];
      let next: string | undefined;
      do {
        const response = await fetchQueue({ limit: 200, sort: 'createdAt', cursor: next }, (input, init) => fetch(input, { ...init, signal: controller.signal }));
        rows.push(...response.items.map(job => ({ job })));
        next = response.nextCursor ?? undefined;
      } while (next && !controller.signal.aborted);
      const groups = await fetchCollections((input, init) => fetch(input, { ...init, signal: controller.signal }));
      if (!controller.signal.aborted) { collectionRows = rows; collectionGroups = groups; }
    } catch (err) {
      if (!controller.signal.aborted) actionError = err instanceof Error ? err.message : 'Could not load collections.';
    } finally {
      if (!controller.signal.aborted) collectionsLoading = false;
    }
  }

  async function changeView(next: 'jobs' | 'collections') {
    view = next;
    selectedCollection = null;
    query = '';
    selectedFilters = ['all'];
    sourceFilter = 'all';
    resetPaging();
    if (searchTimer) window.clearTimeout(searchTimer);
    if (next === 'collections') await loadCollections();
    else await applyQueueParams();
  }

  async function openCollection(id: string) {
    if (!collectionRows.some(row => row.job.correlationId === id)) await loadCollections();
    selectedCollection = id;
    view = 'jobs';
    query = '';
    selectedFilters = ['all'];
    sourceFilter = 'all';
    resetPaging();
  }

  onMount(() => {
    queue.connect();
    void fetchCollections().then(groups => { collectionGroups = groups; }).catch(() => {});
    void loadStatusCounts().catch(() => {});
    listOptionPresets()
      .then((presets) => {
        optionPresetsByKey = new Map(presets.map((preset) => [preset.key, preset.name]));
      })
      .catch(() => {});
    // Resync when the tab wakes up: state changes that fired while the SSE
    // connection was dead (e.g. laptop sleep) are live-only and never replayed.
    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        if (view === 'collections' || selectedCollection) void loadCollections();
        else void queue.refresh().catch(() => {});
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    const collectionRefresh = window.setInterval(() => {
      if (document.visibilityState !== 'visible') return;
      if (view === 'collections' || selectedCollection) void loadCollections();
      else void fetchCollections().then(groups => { collectionGroups = groups; }).catch(() => {});
    }, 15_000);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      window.clearInterval(collectionRefresh);
      if (searchTimer) {
        window.clearTimeout(searchTimer);
      }
    };
  });

  onDestroy(() => {
    collectionController?.abort();
    queue.disconnect();
    unsubscribe();
  });

  async function refreshQueue() {
    actionError = null;
    try {
      if (view === 'collections' || selectedCollection) await loadCollections();
      else {
        await queue.refresh();
        await loadStatusCounts();
      }
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Could not refresh the queue.';
    }
  }

  async function applyQueueParams() {
    if (selectedCollection || view === 'collections') return;
    await queue.setParams({
      stateGroup: 'all',
      statuses: allSelected ? undefined : selectedStatuses.join(','),
      sourceKind: sourceFilter === 'all' ? undefined : sourceFilter,
      q: query.trim() || undefined,
      limit: pageSize,
      cursor,
      sort: selectedFilters.length === 1 && selectedFilters[0] === 'queued' ? 'priority' : 'createdAt'
    });
    await loadStatusCounts();
  }

  async function loadStatusCounts() {
    const counts = await Promise.all(['all', ...statusOptions.map(option => option.key)].map(async stateGroup => {
      const result = await fetchQueue({ stateGroup: stateGroup as FilterKey, sourceKind: sourceFilter === 'all' ? undefined : sourceFilter, q: query.trim() || undefined, limit: 1 });
      return [stateGroup, result.totalCount] as const;
    }));
    statusCounts = Object.fromEntries(counts);
  }

  function resetPaging(): void {
    page = 1;
    cursor = undefined;
    cursorStack = [];
  }

  async function changeFilter(filter: FilterKey): Promise<void> {
    if (filter === 'all') selectedFilters = allSelected ? [] : ['all'];
    else if (allSelected) selectedFilters = statusOptions.filter(option => option.key !== filter).map(option => option.key);
    else selectedFilters = selectedFilters.includes(filter)
      ? selectedFilters.filter(key => key !== filter && key !== 'all')
      : [...selectedFilters.filter(key => key !== 'all'), filter];
    if (statusOptions.every(option => selectedFilters.includes(option.key))) selectedFilters = ['all'];
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
    if (selectedCollection) { if (hasNext) page += 1; return; }
    if (!queueState.nextCursor) {
      return;
    }
    cursorStack = [...cursorStack, cursor ?? ''];
    cursor = queueState.nextCursor;
    page += 1;
    await applyQueueParams();
  }

  async function previousPage(): Promise<void> {
    if (selectedCollection) { page = Math.max(1, page - 1); return; }
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

  async function stopDownloadGroup(correlationId: string) {
    await runAction(correlationId, 'group-stop', async () => {
      await stopGroup(correlationId, 'Stopped from the jobs page.');
    });
  }

  async function startDownloadGroup(correlationId: string) {
    await runAction(correlationId, 'group-start', async () => {
      await startGroup(correlationId);
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

  function openCleanup() {
    cleanupError = null;
    cleanupNotice = null;
    cleanupRetentionDays = '';
    cleanupIncludeFailed = false;
    cleanupOpen = true;
  }

  async function submitCleanup() {
    // bind:value on a number input yields a number once the field has content, and '' or null
    // while it is empty — so never assume a string here.
    let retentionDays: number | undefined;
    const raw = cleanupRetentionDays;
    if (raw !== '' && raw !== null && raw !== undefined) {
      const parsed = Number(raw);
      if (!Number.isInteger(parsed) || parsed < 0) {
        cleanupError = 'Retention must be a whole number of days, zero or greater.';
        return;
      }
      retentionDays = parsed;
    }

    cleanupError = null;
    cleanupBusy = true;
    try {
      await cleanupHistory({ retentionDays, includeFailed: cleanupIncludeFailed });
      cleanupOpen = false;
      cleanupNotice = 'Cleanup queued. Watch its progress and deletion counts on Jobs > Background.';
      // The purge runs in the background, so this refresh only shows whatever finished immediately.
      await applyQueueParams();
    } catch (err) {
      cleanupError = err instanceof Error ? err.message : 'Could not queue the cleanup.';
    } finally {
      cleanupBusy = false;
    }
  }

  async function runAction(jobId: string, action: string, fn: () => Promise<void>) {
    actionError = null;
    actionBusy = { ...actionBusy, [jobId]: action };
    try {
      await fn();
      if (selectedCollection) await loadCollections();
      else await applyQueueParams();
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

<section class="overflow-hidden rounded-lg border border-base-300 bg-base-200/40" aria-labelledby="downloads-title">
  <header class="flex flex-wrap items-center gap-3 px-5 pt-5">
    <h2 id="downloads-title" class="text-xl font-semibold tracking-tight">Downloads</h2>
    <span class={['inline-flex items-center gap-1.5 text-xs', queueState.connected ? 'text-success' : 'text-base-content/50']} role="status">
      <span class={['h-1.5 w-1.5 rounded-full', queueState.connected ? 'bg-success' : 'bg-base-content/30']}></span>
      {queueState.connected ? 'Live' : 'Reconnecting'}
    </span>
    <div class="ml-auto flex gap-2">
      <button class="btn btn-sm btn-primary text-xs" onclick={refreshQueue} disabled={queueState.loading || collectionsLoading}><RefreshCw class="h-3.5 w-3.5" />Refresh</button>
      <button class="btn btn-sm btn-primary text-xs" onclick={openCleanup}><Trash2 class="h-3.5 w-3.5" />Clean up</button>
    </div>
  </header>
  <nav class="tabs tabs-border mt-2 border-b border-base-300 px-5" aria-label="Download views">
    <button class={['tab px-0 mr-6 text-sm', view === 'jobs' && !selectedCollection && 'tab-active']} aria-pressed={view === 'jobs' && !selectedCollection} onclick={() => changeView('jobs')}>All jobs</button>
    <button class={['tab px-0 text-sm', view === 'collections' && 'tab-active']} aria-pressed={view === 'collections'} onclick={() => changeView('collections')}>Collections</button>
  </nav>
  <div class="p-3.5 sm:p-5">
    {#if cleanupNotice}
      <div class="alert alert-info mb-4 text-sm" role="status"><span>{cleanupNotice} <a class="link" href="/jobs/background">View background jobs</a></span></div>
    {/if}
    {#if selectedGroup}
      <nav class="mb-3 flex flex-wrap gap-2 text-xs text-base-content/55" aria-label="Breadcrumb">
        <button class="link" onclick={() => changeView('jobs')}>All jobs</button><span>/</span><button class="link" onclick={() => changeView('collections')}>Collections</button><span>/</span><span>{selectedGroup.name}</span>
      </nav>
      <h3 class="text-lg font-semibold">{selectedGroup.name}</h3>
      {#if selectedGroup.sourceUrl}
        <a class="btn btn-sm btn-outline mt-2 gap-1.5 text-xs" href={selectedGroup.sourceUrl} target="_blank" rel="noopener noreferrer"><ExternalLink class="h-3.5 w-3.5" />View source</a>
      {/if}
      <p class="mt-1 text-xs text-base-content/55">{selectedGroup.rows.length} jobs · {selectedGroup.done} complete · {selectedGroup.active} active · {selectedGroup.queued} queued · {selectedGroup.failed} failed</p>
      <div class="my-3 flex flex-wrap gap-2">
        <button class="btn btn-sm btn-primary text-xs" onclick={() => stopDownloadGroup(selectedGroup!.id)} disabled={Boolean(actionBusy[selectedGroup.id]) || !(selectedGroup.active + selectedGroup.queued || selectedGroup.status === 'Queued' || selectedGroup.status === 'Expanding')}>Stop group</button>
        <button class="btn btn-sm btn-primary text-xs" onclick={() => startDownloadGroup(selectedGroup!.id)} disabled={Boolean(actionBusy[selectedGroup.id]) || !selectedGroup.rows.length}>Retry failed / stopped</button>
      </div>
      {#if !selectedGroup.rows.length}<p class="mb-3 text-sm text-base-content/60">{selectedGroup.status === 'Queued' ? 'Waiting for worker before discovery starts.' : 'Discovering collection items…'}</p>{/if}
      <progress class="progress progress-success mb-3 h-1 w-full" value={selectedGroup.done} max={selectedGroup.rows.length} aria-label="Completed jobs in collection"></progress>
    {:else if view === 'collections'}
      <h3 class="text-lg font-semibold">Collections</h3>
      <p class="mt-1 text-xs text-base-content/55">Channel and playlist submissions</p>
    {/if}

    {#if view === 'jobs'}
      <fieldset class="mb-3" aria-label="Job statuses">
        <label class="mb-3 flex w-fit cursor-pointer items-center gap-2 text-sm">
          <input type="checkbox" class="checkbox checkbox-sm" checked={allSelected} onchange={() => changeFilter('all')} />
          <span>All</span><span class="text-base-content/50 tabular-nums">{allCount?.toLocaleString() ?? '…'}</span>
        </label>
        <div class="flex flex-wrap gap-2">
          {#each tabs as tab}
            <label class="flex cursor-pointer items-center gap-2 rounded-lg border border-base-300 bg-base-100 px-3 py-2.5 text-sm">
              <input type="checkbox" class="checkbox checkbox-sm" checked={allSelected || selectedFilters.includes(tab.key)} onchange={() => changeFilter(tab.key)} />
              <span>{tab.label}</span><span class="text-base-content/50 tabular-nums">{tab.count?.toLocaleString() ?? '…'}</span>
            </label>
          {/each}
        </div>
        <p class="mt-3 text-xs text-base-content/60" aria-live="polite">Showing <span class="font-medium">{allSelected ? 'All statuses' : tabs.filter(tab => selectedFilters.includes(tab.key)).map(tab => tab.label).join(', ') || 'No statuses selected'}</span></p>
      </fieldset>
    {/if}
    <div class="my-3.5 flex flex-wrap gap-2">
      <label class="input input-sm flex min-w-40 flex-1 items-center gap-2 bg-base-100">
        <Search class="h-3.5 w-3.5 shrink-0 text-base-content/40" />
        <input class="w-full" type="search" bind:value={query} oninput={scheduleSearch} aria-label={view === 'collections' ? 'Search collections' : 'Search jobs'} placeholder={view === 'collections' ? 'Find a collection…' : selectedCollection ? 'Search jobs in this collection…' : 'Search source or job ID…'} />
      </label>
      {#if view === 'jobs'}<Select items={pageSizeOptions} bind:value={pageSize} onchange={changePageSize} aria-label="Jobs per page" class="select-sm w-36 bg-base-100 text-xs" />{/if}
    </div>
    {#if view === 'jobs'}
      <div class="mb-3 flex flex-wrap gap-1" aria-label="Job source filters">
        {#each sourceTabs as tab}<button class={['btn btn-xs btn-ghost font-normal', sourceFilter === tab.key ? 'text-primary' : 'text-base-content/50']} aria-pressed={sourceFilter === tab.key} onclick={() => changeSourceFilter(tab.key)}>{tab.label}</button>{/each}
      </div>
    {/if}
    {#if queueState.error || actionError}<div class="alert alert-error my-4 text-sm" role="alert"><CircleAlert class="h-4 w-4" /><span>{actionError ?? queueState.error}</span></div>{/if}
    {#if view === 'jobs' && !selectedCollection && holdingCollectionCards.length}
      <div class="mb-4 space-y-2" aria-label="Queued collections">
        {#each holdingCollectionCards as group (group.groupId)}
          <article class="card border border-base-300 bg-base-100 p-3.5">
            <div class="flex items-center gap-2"><Folder class="h-4 w-4 text-base-content/50" /><span class="badge badge-sm badge-ghost">{group.kind}</span><span class="badge badge-sm">{group.status}</span></div>
            <p class="mt-2 break-all text-sm font-medium">{group.sourceUrl}</p>
            <p class="mt-1 text-xs text-base-content/60">{group.status === 'Queued' ? 'Waiting for worker before discovery starts' : 'Discovering collection items'}</p>
            <button class="btn btn-sm btn-accent mt-3 w-fit text-xs" onclick={() => openCollection(group.correlationId)}>Open collection</button>
          </article>
        {/each}
      </div>
    {/if}
    {#if (collectionsLoading && !collections.length) || (queueState.loading && !queueState.rows.length && view === 'jobs' && !selectedCollection)}
      <div class="flex justify-center py-16" role="status" aria-label="Loading jobs"><span class="loading loading-spinner loading-md"></span></div>
    {:else if view === 'collections'}
      <div class="space-y-2">
        {#each visibleCollections as group (group.id)}
          <article class="card grid grid-cols-[2.25rem_minmax(0,1fr)] gap-3 rounded-lg border border-base-300 bg-base-100 p-3.5">
            <div class="flex h-8 items-center justify-center rounded bg-base-200 text-base-content/50"><Folder class="h-4 w-4" /></div>
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2"><button class="link text-left text-sm font-semibold text-primary" onclick={() => openCollection(group.id)}>{group.name}</button><span class="badge badge-sm badge-ghost rounded text-[11px]">{group.kind}</span></div>
              <p class="mt-1 text-xs text-base-content/55">{group.createdAt ? new Date(group.createdAt).toLocaleString() : ''}</p>
              {#if !group.rows.length}<p class="my-3 text-xs text-base-content/60">{group.status === 'Queued' ? 'Waiting for worker before discovery starts' : 'Discovering collection items'}</p>
              {:else}<p class="my-3 flex flex-wrap gap-x-4 gap-y-1 text-xs text-base-content/60"><span>{group.rows.length} jobs</span><span>{group.done} complete</span><span>{group.active} active</span><span>{group.queued} queued</span><span>{group.failed} failed</span></p>{/if}
              <progress class="progress progress-success block h-1 w-full" value={group.done} max={group.rows.length} aria-label="Completed jobs"></progress>
              <button class="btn btn-sm btn-accent mt-2 text-xs" onclick={() => openCollection(group.id)}>Open collection <ChevronRight class="h-3.5 w-3.5" /></button>
            </div>
          </article>
        {:else}<p class="py-10 text-center text-sm text-base-content/60">No collections match this view.</p>{/each}
      </div>
    {:else if visibleRows.length === 0 && holdingCollectionCards.length === 0}
      <div class="py-10 text-center"><Server class="mx-auto h-8 w-8 text-base-content/30" /><p class="mt-4 text-sm font-semibold">No jobs match this view</p><p class="mt-1 text-xs text-base-content/50">Queue a download or change the filters.</p></div>
    {:else}
      <div class="space-y-2">
        {#each visibleRows as row (row.job.jobId)}
          <JobRow {row} {optionPresetsByKey} busyAction={actionBusy[row.job.jobId]} onstop={stopDownload} onstart={startDownload} onclearprovider={clearDownloadProvider} onpriority={updatePriority} oncollection={openCollection} />
        {/each}
      </div>
      <div class="mt-4 flex flex-wrap items-center justify-between gap-3">
        <p class="text-xs tabular-nums text-base-content/55">{Math.min((page - 1) * pageSize + 1, matchingCount)}–{Math.min(page * pageSize, matchingCount)} of {matchingCount} jobs</p>
        <div class="flex gap-2"><button class="btn btn-sm bg-base-100 text-xs font-normal" disabled={page <= 1 || queueState.loading} onclick={previousPage}><ChevronLeft class="h-3.5 w-3.5" />Previous</button><button class="btn btn-sm bg-base-100 text-xs font-normal" disabled={!hasNext || queueState.loading} onclick={nextPage}>Next<ChevronRight class="h-3.5 w-3.5" /></button></div>
      </div>
    {/if}
  </div>
</section>

<Modal bind:open={cleanupOpen} title="Clean up download history" size="md">
  <div class="space-y-4 text-sm">
    <p class="text-base-content/70">
      Deletes finished download jobs along with their runs, artifacts, warnings, event history and
      progress log, plus the groups they belonged to. Queued and running work is never touched, and a
      group is all-or-nothing — nothing is removed from a playlist that has not fully settled.
    </p>

    <label class="block">
      <span class="text-xs font-semibold uppercase tracking-[0.08em] text-base-content/50">Retention (days)</span>
      <input
        type="number"
        min="0"
        step="1"
        placeholder="30"
        class="input input-bordered mt-1.5 w-full"
        bind:value={cleanupRetentionDays}
        disabled={cleanupBusy}
      />
      <span class="mt-1.5 block text-xs text-base-content/50">
        Only work that finished longer ago than this is removed. Leave empty for the 30-day default, or
        enter 0 to purge everything eligible.
      </span>
    </label>

    <label class="flex cursor-pointer items-start gap-3 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/40 p-3">
      <input
        type="checkbox"
        class="checkbox checkbox-sm mt-0.5 checkbox-error"
        bind:checked={cleanupIncludeFailed}
        disabled={cleanupBusy}
      />
      <span>
        <span class="font-semibold text-base-content">Also delete failed and stopped jobs</span>
        <span class="mt-0.5 block text-xs text-base-content/60">
          They cannot be retried afterwards. Start rebuilds a job from its event history, which this
          deletes.
        </span>
      </span>
    </label>

    {#if cleanupError}
      <div class="alert alert-error" role="alert">
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{cleanupError}</span>
      </div>
    {/if}
  </div>

  {#snippet footer()}
    <div class="flex justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" onclick={() => (cleanupOpen = false)} disabled={cleanupBusy}>
        Cancel
      </button>
      <button
        class={['btn btn-sm text-xs', cleanupIncludeFailed ? 'btn-error' : 'btn-primary']}
        onclick={submitCleanup}
        disabled={cleanupBusy}
      >
        {#if cleanupBusy}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <Trash2 class="mr-1.5 h-4 w-4" />
        {/if}
        {cleanupIncludeFailed ? 'Delete including failed' : 'Clean up'}
      </button>
    </div>
  {/snippet}
</Modal>
