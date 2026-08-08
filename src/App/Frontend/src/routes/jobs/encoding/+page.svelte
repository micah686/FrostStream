<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    ChevronLeft,
    ChevronRight,
    CircleAlert,
    Play,
    RefreshCw,
    RotateCw,
    Square,
    Video
  } from '@lucide/svelte';
  import { createEncodingQueueStore, type EncodingQueueState } from '$lib/stores/encodingQueue';
  import type { RenditionKind, RenditionStatus } from '$lib/api/encodingQueue';
  import { formatBytes, formatDuration, formatRelativeDate } from '$lib/media';

  type StatusFilterKey = 'all' | RenditionStatus;
  type KindFilterKey = 'all' | RenditionKind;

  const queue = createEncodingQueueStore();
  let queueState = $state<EncodingQueueState>({
    rows: [],
    totalCount: 0,
    nextCursor: null,
    connected: false,
    loading: true,
    error: null
  });
  let statusFilter = $state<StatusFilterKey>('all');
  let kindFilter = $state<KindFilterKey>('all');
  let query = $state('');
  let storageKey = $state('');
  let pageSize = $state(50);
  let page = $state(1);
  let cursor = $state<string | undefined>(undefined);
  let cursorStack = $state<string[]>([]);
  let actionError = $state<string | null>(null);
  let searchTimer: ReturnType<typeof setTimeout> | undefined;

  const unsubscribe = queue.subscribe((value) => {
    queueState = value;
  });

  const pageSizeOptions = [
    { value: 25, name: '25 per page' },
    { value: 50, name: '50 per page' },
    { value: 100, name: '100 per page' }
  ];

  const statusTabs: Array<{ key: StatusFilterKey; label: string }> = [
    { key: 'all', label: 'All' },
    { key: 'Processing', label: 'Processing' },
    { key: 'Pending', label: 'Queued' },
    { key: 'Ready', label: 'Ready' },
    { key: 'Failed', label: 'Failed' }
  ];

  const kindTabs: Array<{ key: KindFilterKey; label: string }> = [
    { key: 'all', label: 'All kinds' },
    { key: 'Stream', label: 'Stream (HLS)' },
    { key: 'Audio', label: 'Audio (Opus)' }
  ];

  onMount(() => {
    queue.connect();
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
      actionError = err instanceof Error ? err.message : 'Could not refresh the encoding queue.';
    }
  }

  async function applyQueueParams() {
    await queue.setParams({
      kind: kindFilter === 'all' ? undefined : kindFilter,
      status: statusFilter === 'all' ? undefined : statusFilter,
      storageKey: storageKey.trim() || undefined,
      q: query.trim() || undefined,
      limit: pageSize,
      cursor
    });
  }

  function resetPaging(): void {
    page = 1;
    cursor = undefined;
    cursorStack = [];
  }

  async function changeStatusFilter(filter: StatusFilterKey): Promise<void> {
    if (statusFilter === filter) return;
    statusFilter = filter;
    resetPaging();
    await applyQueueParams();
  }

  async function changeKindFilter(filter: KindFilterKey): Promise<void> {
    if (kindFilter === filter) return;
    kindFilter = filter;
    resetPaging();
    await applyQueueParams();
  }

  function scheduleSearch(): void {
    resetPaging();
    if (searchTimer) window.clearTimeout(searchTimer);
    searchTimer = setTimeout(() => void applyQueueParams(), 300);
  }

  async function changePageSize(): Promise<void> {
    pageSize = Number(pageSize);
    resetPaging();
    await applyQueueParams();
  }

  async function nextPage(): Promise<void> {
    if (!queueState.nextCursor) return;
    cursorStack = [...cursorStack, cursor ?? ''];
    cursor = queueState.nextCursor;
    page += 1;
    await applyQueueParams();
  }

  async function previousPage(): Promise<void> {
    if (page <= 1) return;
    const previousStack = cursorStack.slice(0, -1);
    const previousCursor = cursorStack.at(-1);
    cursorStack = previousStack;
    cursor = previousCursor || undefined;
    page = Math.max(1, page - 1);
    await applyQueueParams();
  }

  function statusBadgeClass(status: RenditionStatus): string {
    switch (status) {
      case 'Ready':
        return 'badge-success text-success-content';
      case 'Failed':
        return 'badge-error text-error-content';
      default:
        return 'badge-primary text-primary-content';
    }
  }

  function progressPercent(row: EncodingQueueState['rows'][number]): number {
    if (row.item.status === 'Ready') return 100;
    return Math.min(100, Math.max(0, row.progress?.percent ?? 0));
  }

  function progressLine(frame: NonNullable<EncodingQueueState['rows'][number]['progress']>): string {
    const parts = [frame.phase];
    if (frame.percent !== null) parts.push(`${Math.round(frame.percent)}%`);
    if (frame.speedX !== null) parts.push(`${frame.speedX.toFixed(1)}x`);
    const eta = frame.etaSeconds !== null ? formatDuration(Math.round(frame.etaSeconds)) : null;
    if (eta) parts.push(`eta ${eta}`);
    return parts.join(' · ');
  }
</script>

<svelte:head>
  <title>Encoding · Jobs · FrostStream</title>
</svelte:head>

<div aria-labelledby="encoding-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="encoding-title" class="text-lg font-semibold tracking-tight text-base-content">Encoding</h2>
      <p class="mt-1 text-sm text-base-content/50">
        Stream (HLS) and audio (Opus) rendition jobs · page {page} · {queueState.rows.length} shown · {queueState.totalCount} matching
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      {#if queueState.connected}
        <span class="badge badge-success h-8 px-3 text-xs font-semibold text-success-content">SSE Live</span>
      {/if}
      <button class="btn btn-sm btn-neutral text-xs" onclick={refreshQueue} disabled={queueState.loading}>
        {#if queueState.loading}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <RefreshCw class="mr-1.5 h-4 w-4" />
        {/if}
        Refresh
      </button>
    </div>
  </div>

  <div class="mt-6 flex gap-2 overflow-x-auto pb-1" aria-label="Encoding kind filters">
    {#each kindTabs as tab}
      <button
        type="button"
        onclick={() => changeKindFilter(tab.key)}
        class={['btn btn-sm shrink-0 text-xs', kindFilter === tab.key ? 'btn-primary' : 'btn-neutral']}
        aria-pressed={kindFilter === tab.key}
      >
        {tab.label}
      </button>
    {/each}
  </div>

  <div class="mt-3 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
    <div class="flex gap-2 overflow-x-auto pb-1" aria-label="Encoding status filters">
      {#each statusTabs as tab}
        <button
          type="button"
          onclick={() => changeStatusFilter(tab.key)}
          class={['btn btn-sm shrink-0 gap-2 text-xs', statusFilter === tab.key ? 'btn-primary' : 'btn-neutral']}
          aria-current={statusFilter === tab.key ? 'page' : undefined}
        >
          {tab.label}
        </button>
      {/each}
    </div>

    <div class="flex w-full flex-col gap-2 sm:flex-row lg:w-auto">
      <Select items={pageSizeOptions} bind:value={pageSize} onchange={changePageSize} aria-label="Jobs per page" class="h-10 w-full text-sm sm:w-40" />
      <input class="input w-full h-10 text-sm sm:w-48" type="search" bind:value={storageKey} oninput={scheduleSearch} aria-label="Filter by storage key" placeholder="Storage key..." />
      <input class="input w-full h-10 text-sm lg:w-80" type="search" bind:value={query} oninput={scheduleSearch} aria-label="Search encoding jobs" placeholder="Search title or media guid..." />
    </div>
  </div>

  {#if queueState.error || actionError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionError ?? queueState.error}</span>
    </div>
  {/if}

  {#if queueState.loading && queueState.rows.length === 0}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if queueState.rows.length === 0}
    <div class="mt-8 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <Video class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No encoding jobs match this view</p>
      <p class="mt-1 text-sm text-base-content/50">Encode a channel or watch/cast an item to queue one.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each queueState.rows as row (row.item.renditionId)}
        {@const percent = progressPercent(row)}
        <article class="card border border-base-300/90 bg-base-200/45 p-4 transition">
          <div class="grid gap-3 md:grid-cols-[minmax(0,1fr)_5rem_minmax(8.5rem,auto)] md:items-center">
            <div class="min-w-0">
              <div class="flex min-w-0 items-center gap-2">
                {#if row.item.kind === 'Stream'}
                  <span class="badge badge-sm shrink-0 badge-accent text-[10px] font-bold text-accent-content">HLS</span>
                {/if}
                <h2 class="min-w-0 truncate text-sm font-semibold text-base-content" title={row.item.title}>
                  {row.item.title}
                </h2>
                <span class={['badge badge-sm shrink-0 text-[10px] font-bold', statusBadgeClass(row.item.status)]}>
                  {row.item.status}
                </span>
              </div>
              <p class="mt-1 truncate text-xs text-base-content/50">
                {row.item.storageKey}
                {#if row.item.storagePath} · {row.item.storagePath}{/if}
              </p>
              {#if row.progress}
                <p class="mt-1 truncate text-[11px] text-base-content/50">{progressLine(row.progress)}</p>
              {/if}
              {#if row.item.status === 'Failed' && row.item.errorMessage}
                <p class="mt-2 line-clamp-1 text-xs text-error">{row.item.errorMessage}</p>
              {/if}
              <p class="mt-2 text-[11px] text-base-content/40">
                {row.item.sizeBytes ? formatBytes(row.item.sizeBytes) : '—'}
                {#if row.item.durationSeconds}· {formatDuration(row.item.durationSeconds)}{/if}
                · updated {formatRelativeDate(row.item.updatedAt)}
              </p>
            </div>

            <div class="flex items-center justify-center">
              <div
                class="radial-progress text-primary"
                style={`--value:${percent}; --size:3.5rem;`}
                role="progressbar"
                aria-valuenow={Math.round(percent)}
                aria-valuemin="0"
                aria-valuemax="100"
              >
                <span class="text-[11px] font-semibold text-base-content">{Math.round(percent)}%</span>
              </div>
            </div>

            <div class="flex flex-wrap items-center justify-end gap-1.5">
              <a
                href={`/watch/${row.item.mediaGuid}`}
                class="btn btn-sm btn-neutral text-xs"
                title="Watch"
                aria-label="Watch"
              >
                <Play class="h-4 w-4" />
                Watch
              </a>
              {#if row.item.status === 'Processing' || row.item.status === 'Pending'}
                <button type="button" class="btn btn-sm btn-neutral text-xs" title="Stop encode" aria-label="Stop encode">
                  <Square class="h-4 w-4" />
                  Stop
                </button>
              {/if}
              {#if row.item.status === 'Failed'}
                <button type="button" class="btn btn-sm btn-neutral text-xs" title="Retry encode" aria-label="Retry encode">
                  <RotateCw class="h-4 w-4" />
                  Retry
                </button>
              {/if}
            </div>
          </div>
        </article>
      {/each}
    </div>

    <div class="mt-6 flex flex-col gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
      <p class="text-xs text-base-content/40">
        Showing {Math.min((page - 1) * pageSize + 1, queueState.totalCount)}-{Math.min(page * pageSize, queueState.totalCount)}
        of {queueState.totalCount}
      </p>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-neutral text-xs" disabled={page <= 1 || queueState.loading} onclick={previousPage}>
          <ChevronLeft class="mr-1 h-3.5 w-3.5" />
          Previous
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={!queueState.nextCursor || queueState.loading} onclick={nextPage}>
          Next
          <ChevronRight class="ml-1 h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  {/if}
</div>
