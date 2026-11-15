<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Select } from '#lib/components/ui/index.js';
  import {
    ChevronLeft,
    ChevronRight,
    CircleAlert,
    Music2,
    Play,
    RefreshCw,
    Search,
    Video
  } from '@lucide/svelte';
  import { createEncodingQueueStore, type EncodingQueueState } from '#lib/stores/encodingQueue.js';
  import type { RenditionKind, RenditionStatus } from '#lib/api/encodingQueue.js';
  import { formatBytes, formatDuration, formatRelativeDate } from '#lib/media.js';

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
        return 'badge-soft badge-success';
      case 'Failed':
        return 'badge-soft badge-error';
      default:
        return status === 'Processing' ? 'badge-soft badge-primary' : 'badge-ghost';
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

<section class="overflow-hidden rounded-lg border border-base-300 bg-base-200/40" aria-labelledby="encoding-title">
  <header class="flex flex-wrap items-center gap-3 px-5 pt-5">
    <h2 id="encoding-title" class="text-xl font-semibold tracking-tight">Encoding</h2>
    <span class={['inline-flex items-center gap-1.5 text-xs', queueState.connected ? 'text-success' : 'text-base-content/50']} role="status">
      <span class={['h-1.5 w-1.5 rounded-full', queueState.connected ? 'bg-success' : 'bg-base-content/30']}></span>
      {queueState.connected ? 'Live' : 'Reconnecting'}
    </span>
    <button class="btn btn-sm btn-primary ml-auto text-xs" onclick={refreshQueue} disabled={queueState.loading}>
      {#if queueState.loading}<span class="loading loading-spinner loading-xs"></span>{:else}<RefreshCw class="h-3.5 w-3.5" />{/if}
      Refresh
    </button>
  </header>

  <nav class="tabs tabs-border mt-2 border-b border-base-300 px-5" aria-label="Encoding kind filters">
    {#each kindTabs as tab}
      <button type="button" onclick={() => changeKindFilter(tab.key)} class={['tab mr-6 px-0 text-sm', kindFilter === tab.key && 'tab-active']} aria-pressed={kindFilter === tab.key}>{tab.label}</button>
    {/each}
  </nav>

  <div class="p-3.5 sm:p-5">
    <div class="flex flex-wrap gap-2" aria-label="Encoding status filters">
      {#each statusTabs as tab}
        <button type="button" onclick={() => changeStatusFilter(tab.key)} class={['btn btn-sm text-xs font-normal', statusFilter === tab.key ? 'btn-primary' : 'bg-base-100']} aria-pressed={statusFilter === tab.key}>{tab.label}</button>
      {/each}
    </div>

    <div class="my-3.5 flex flex-wrap gap-2">
      <label class="input input-sm flex min-w-40 flex-1 items-center gap-2 bg-base-100">
        <Search class="h-3.5 w-3.5 shrink-0 text-base-content/40" />
        <input class="w-full" type="search" bind:value={query} oninput={scheduleSearch} aria-label="Search encoding jobs" placeholder="Search title or media GUID…" />
      </label>
      <input class="input input-sm min-w-36 flex-1 bg-base-100 text-xs sm:max-w-48" type="search" bind:value={storageKey} oninput={scheduleSearch} aria-label="Filter by storage key" placeholder="Storage key…" />
      <Select items={pageSizeOptions} bind:value={pageSize} onchange={changePageSize} aria-label="Jobs per page" class="select-sm w-36 bg-base-100 text-xs" />
    </div>

  {#if queueState.error || actionError}
    <div
      class="alert alert-error my-4 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionError ?? queueState.error}</span>
    </div>
  {/if}

  {#if queueState.loading && queueState.rows.length === 0}
    <div class="flex justify-center py-16">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if queueState.rows.length === 0}
    <div class="py-10 text-center">
      <Video class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No encoding jobs match this view</p>
      <p class="mt-1 text-sm text-base-content/50">Encode a channel or watch/cast an item to queue one.</p>
    </div>
  {:else}
    <div class="space-y-2">
      {#each queueState.rows as row (row.item.renditionId)}
        {@const percent = progressPercent(row)}
        <article class="card rounded-lg border border-base-300 bg-base-100 p-3.5">
          <div class="grid grid-cols-[2.25rem_minmax(0,1fr)] gap-3 max-[470px]:grid-cols-1">
            <div class="flex h-8 items-center justify-center rounded bg-base-200 text-base-content/50 max-[470px]:hidden">{#if row.item.kind === 'Stream'}<Video class="h-4 w-4" />{:else}<Music2 class="h-4 w-4" />{/if}</div>
            <div class="min-w-0">
              <h3 class="break-words text-sm font-semibold text-base-content">{row.item.title}</h3>
              <p class="mt-1 break-all text-xs text-base-content/55">
                {row.item.storageKey}
                {#if row.item.storagePath} · {row.item.storagePath}{/if}
              </p>
              <div class="mt-2 flex flex-wrap items-center gap-2 text-xs text-base-content/60">
                <span class={['badge badge-sm rounded text-[11px]', statusBadgeClass(row.item.status)]}>{row.item.status === 'Pending' ? 'Queued' : row.item.status}</span>
                <span>{row.item.kind === 'Stream' ? 'Stream · HLS' : 'Audio · Opus'}</span>
                {#if row.progress}<span>{progressLine(row.progress)}</span>{/if}
              </div>
              {#if row.item.status === 'Processing'}
                <progress class="progress progress-primary mt-3 block h-1 w-full" value={percent} max="100" aria-label="Encoding progress"></progress>
              {/if}
              {#if row.item.status === 'Failed' && row.item.errorMessage}
                <p class="mt-2 break-words text-xs text-error">{row.item.errorMessage}</p>
              {/if}
              <p class="mt-2 text-xs text-base-content/55">
                {row.item.sizeBytes ? formatBytes(row.item.sizeBytes) : '—'}
                {#if row.item.durationSeconds}· {formatDuration(row.item.durationSeconds)}{/if}
                · updated {formatRelativeDate(row.item.updatedAt)}
              </p>
              <div class="mt-3 flex flex-wrap items-center gap-2">
                <a href={`/watch/${row.item.mediaGuid}`} class="btn btn-sm btn-accent text-xs"><Play class="h-4 w-4" />Watch</a>
              </div>
            </div>
          </div>
        </article>
      {/each}
    </div>

    <div class="mt-4 flex flex-wrap items-center justify-between gap-3">
      <p class="text-xs tabular-nums text-base-content/55">
        Showing {Math.min((page - 1) * pageSize + 1, queueState.totalCount)}-{Math.min(page * pageSize, queueState.totalCount)}
        of {queueState.totalCount}
      </p>
      <div class="flex gap-2">
        <button class="btn btn-sm bg-base-100 text-xs font-normal" disabled={page <= 1 || queueState.loading} onclick={previousPage}>
          <ChevronLeft class="h-3.5 w-3.5" />
          Previous
        </button>
        <button class="btn btn-sm bg-base-100 text-xs font-normal" disabled={!queueState.nextCursor || queueState.loading} onclick={nextPage}>
          Next
          <ChevronRight class="h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  {/if}
  </div>
</section>
