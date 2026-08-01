<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    ChevronLeft,
    ChevronRight,
    CircleAlert,
    RefreshCw,
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

  function statusClass(status: RenditionStatus): string {
    switch (status) {
      case 'Ready':
        return 'border-success/30 bg-success/10 text-success';
      case 'Failed':
        return 'border-error/30 bg-error/10 text-error';
      case 'Processing':
        return 'border-primary/30 bg-primary/10 text-primary';
      default:
        return 'border-base-content/20 bg-base-200 text-base-content/60';
    }
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
            queueState.connected ? 'bg-success shadow-[0_0_10px_var(--color-success)]' : 'bg-base-300'
          ]}
        ></span>
        {queueState.connected ? 'SSE live' : 'Connecting'}
      </span>
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
        class={[
          'inline-flex h-8 shrink-0 items-center rounded-lg border px-3 text-xs font-semibold transition',
          kindFilter === tab.key
            ? 'border-primary bg-primary text-primary-content shadow-sm'
            : 'border-base-content/25 bg-base-100 text-base-content/70 hover:border-base-content/40 hover:bg-base-200 hover:text-base-content'
        ]}
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
          class={[
            'inline-flex h-9 shrink-0 items-center gap-2 rounded-lg border px-4 text-xs font-semibold transition',
            statusFilter === tab.key
              ? 'border-primary bg-primary text-primary-content shadow-sm'
              : 'border-base-content/25 bg-base-100 text-base-content/80 hover:border-base-content/40 hover:bg-base-200'
          ]}
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
        <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0">
              <div class="flex items-center gap-2">
                <span class="rounded-md bg-base-300/80 px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-[0.06em] text-base-content/60">
                  {row.item.kind === 'Stream' ? 'HLS' : 'Opus'}
                </span>
                <a
                  href={`/watch/${row.item.mediaGuid}`}
                  class="truncate text-sm font-semibold text-base-content hover:text-primary"
                  title={row.item.title}
                >
                  {row.item.title}
                </a>
              </div>
              <p class="mt-1 truncate text-xs text-base-content/50">
                {row.item.storageKey}
                {#if row.item.storagePath} · {row.item.storagePath}{/if}
              </p>
            </div>
            <span class={['shrink-0 rounded-full border px-2.5 py-1 text-[11px] font-semibold', statusClass(row.item.status)]}>
              {row.item.status}
            </span>
          </div>

          {#if row.progress}
            <div class="mt-3">
              <div class="flex justify-between gap-3 text-[11px] text-base-content/50">
                <span>{progressLine(row.progress)}</span>
                {#if row.progress.percent !== null}<span>{Math.round(row.progress.percent)}%</span>{/if}
              </div>
              <div class="mt-1 h-1.5 overflow-hidden rounded-full bg-base-300">
                <div
                  class="h-full rounded-full bg-primary transition-all"
                  style={`width: ${row.progress.percent ?? 0}%`}
                ></div>
              </div>
            </div>
          {:else if row.item.status === 'Failed' && row.item.errorMessage}
            <p class="mt-2 text-xs text-error">{row.item.errorMessage}</p>
          {/if}

          <p class="mt-2 text-[11px] text-base-content/40">
            {row.item.sizeBytes ? formatBytes(row.item.sizeBytes) : '—'}
            {#if row.item.durationSeconds}· {formatDuration(row.item.durationSeconds)}{/if}
            · updated {formatRelativeDate(row.item.updatedAt)}
          </p>
        </div>
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
