<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Button, Input, Select, Spinner } from 'flowbite-svelte';
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    ExclamationCircleOutline,
    RefreshOutline,
    VideoCameraOutline
  } from 'flowbite-svelte-icons';
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
        return 'border-emerald-500/30 bg-emerald-500/10 text-emerald-300';
      case 'Failed':
        return 'border-red-500/30 bg-red-500/10 text-red-300';
      case 'Processing':
        return 'border-blue-500/30 bg-blue-500/10 text-blue-300';
      default:
        return 'border-slate-700 bg-slate-900 text-slate-400';
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
      <h2 id="encoding-title" class="text-lg font-semibold tracking-tight text-white">Encoding</h2>
      <p class="mt-1 text-sm text-slate-500">
        Stream (HLS) and audio (Opus) rendition jobs · page {page} · {queueState.rows.length} shown · {queueState.totalCount} matching
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
            ? 'border-blue-500/60 bg-blue-500/15 text-blue-200'
            : 'border-slate-800 bg-slate-900/60 text-slate-400 hover:bg-slate-800 hover:text-slate-200'
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
            'inline-flex h-9 shrink-0 items-center gap-2 rounded-full px-4 text-xs font-semibold transition',
            statusFilter === tab.key
              ? 'bg-slate-100 text-slate-950'
              : 'bg-slate-800/75 text-slate-300 hover:bg-slate-700'
          ]}
          aria-current={statusFilter === tab.key ? 'page' : undefined}
        >
          {tab.label}
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
        bind:value={storageKey}
        oninput={scheduleSearch}
        aria-label="Filter by storage key"
        placeholder="Storage key..."
        class="h-10 w-full border-slate-800! bg-slate-900/80! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500! sm:w-48"
      />
      <Input
        type="search"
        bind:value={query}
        oninput={scheduleSearch}
        aria-label="Search encoding jobs"
        placeholder="Search title or media guid..."
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
      <VideoCameraOutline class="mx-auto h-10 w-10 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No encoding jobs match this view</p>
      <p class="mt-1 text-sm text-slate-500">Encode a channel or watch/cast an item to queue one.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each queueState.rows as row (row.item.renditionId)}
        <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0">
              <div class="flex items-center gap-2">
                <span class="rounded-md bg-slate-800/80 px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-[0.06em] text-slate-400">
                  {row.item.kind === 'Stream' ? 'HLS' : 'Opus'}
                </span>
                <a
                  href={`/watch/${row.item.mediaGuid}`}
                  class="truncate text-sm font-semibold text-slate-100 hover:text-blue-300"
                  title={row.item.title}
                >
                  {row.item.title}
                </a>
              </div>
              <p class="mt-1 truncate text-xs text-slate-500">
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
              <div class="flex justify-between gap-3 text-[11px] text-slate-500">
                <span>{progressLine(row.progress)}</span>
                {#if row.progress.percent !== null}<span>{Math.round(row.progress.percent)}%</span>{/if}
              </div>
              <div class="mt-1 h-1.5 overflow-hidden rounded-full bg-slate-800">
                <div
                  class="h-full rounded-full bg-blue-500 transition-all"
                  style={`width: ${row.progress.percent ?? 0}%`}
                ></div>
              </div>
            </div>
          {:else if row.item.status === 'Failed' && row.item.errorMessage}
            <p class="mt-2 text-xs text-red-400">{row.item.errorMessage}</p>
          {/if}

          <p class="mt-2 text-[11px] text-slate-600">
            {row.item.sizeBytes ? formatBytes(row.item.sizeBytes) : '—'}
            {#if row.item.durationSeconds}· {formatDuration(row.item.durationSeconds)}{/if}
            · updated {formatRelativeDate(row.item.updatedAt)}
          </p>
        </div>
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
</div>
