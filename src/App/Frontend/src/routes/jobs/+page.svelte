<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Button, Input, Spinner } from 'flowbite-svelte';
  import {
    ArrowsRepeatOutline,
    ArrowUpRightFromSquareOutline,
    FireOutline,
    CheckCircleOutline,
    ClockOutline,
    DownloadOutline,
    ExclamationCircleOutline,
    RefreshOutline,
    ServerOutline,
    StopOutline,
  } from 'flowbite-svelte-icons';
  import { cancelJob, restartJob, setPriority, type DownloadQueueJob, type ProgressFrame } from '$lib/api/downloadQueue';
  import { createDownloadQueueStore, type DownloadQueueState, type QueueRow } from '$lib/stores/downloadQueue';
  import { formatBytes } from '$lib/media';

  type FilterKey = 'all' | 'active' | 'queued' | 'failed' | 'done' | 'cancelled';

  const queue = createDownloadQueueStore();
  let queueState = $state<DownloadQueueState>({
    rows: [],
    totalCount: 0,
    connected: false,
    loading: true,
    error: null
  });
  let activeFilter = $state<FilterKey>('all');
  let query = $state('');
  let actionBusy = $state<Record<string, string>>({});
  let actionError = $state<string | null>(null);
  let now = $state(Date.now());

  const unsubscribe = queue.subscribe((value) => {
    queueState = value;
  });

  const tabs = $derived([
    { key: 'all' as const, label: 'All', count: queueState.rows.length },
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

  const filteredRows = $derived(
    queueState.rows.filter((row) => matchesFilter(row, activeFilter) && matchesQuery(row, query))
  );

  const activeCount = $derived(queueState.rows.filter((row) => isActive(row.job.state)).length);
  const queuedCount = $derived(queueState.rows.filter((row) => isQueued(row.job.state)).length);
  const failedCount = $derived(queueState.rows.filter((row) => isFailed(row.job.state)).length);
  const totalBytes = $derived(
    queueState.rows.reduce((sum, row) => sum + (row.progress?.totalBytes ?? row.job.fileSizeBytes ?? 0), 0)
  );

  onMount(() => {
    queue.connect();
    const timer = window.setInterval(() => {
      now = Date.now();
    }, 1000);
    return () => window.clearInterval(timer);
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
      await queue.refresh();
    } catch (err) {
      actionError = err instanceof Error ? err.message : `Could not ${action} the job.`;
    } finally {
      const { [jobId]: _removed, ...rest } = actionBusy;
      actionBusy = rest;
    }
  }

  function matchesFilter(row: QueueRow, filter: FilterKey): boolean {
    if (filter === 'all') {
      return true;
    }
    if (filter === 'active') {
      return isActive(row.job.state);
    }
    if (filter === 'queued') {
      return isQueued(row.job.state);
    }
    if (filter === 'failed') {
      return isFailed(row.job.state);
    }
    if (filter === 'done') {
      return isDone(row.job.state);
    }
    return isCancelled(row.job.state);
  }

  function matchesQuery(row: QueueRow, value: string): boolean {
    const needle = value.trim().toLowerCase();
    if (!needle) {
      return true;
    }

    const job = row.job;
    return [
      job.sourceUrl,
      job.storageKey,
      job.sourceKind,
      job.state,
      job.jobId,
      job.requestedBy,
      job.failureCode,
      job.failureMessage
    ]
      .filter(Boolean)
      .some((field) => String(field).toLowerCase().includes(needle));
  }

  function normalizeState(state: string): string {
    return state.toLowerCase();
  }

  function isQueued(state: string): boolean {
    return ['queued', 'downloadqueued'].includes(normalizeState(state));
  }

  function isActive(state: string): boolean {
    return [
      'metadatapending',
      'metadataresolved',
      'downloadpending',
      'uploadpending',
      'commitpending',
      'compensating',
      'cancelling'
    ].includes(normalizeState(state));
  }

  function isFailed(state: string): boolean {
    return ['failedtransient', 'failedpermanent', 'deadlettered', 'providerhalted'].includes(normalizeState(state));
  }

  function isDone(state: string): boolean {
    return ['uploaded', 'completed', 'alreadydownloaded'].includes(normalizeState(state));
  }

  function isCancelled(state: string): boolean {
    return ['cancelled', 'ignored'].includes(normalizeState(state));
  }

  function canCancel(job: DownloadQueueJob): boolean {
    return !isDone(job.state) && !isCancelled(job.state) && !isFailed(job.state);
  }

  function canRestart(job: DownloadQueueJob): boolean {
    return normalizeState(job.state) === 'providerhalted' || isCancelled(job.state);
  }

  function canUpdatePriority(job: DownloadQueueJob): boolean {
    return isQueued(job.state);
  }

  function percentFor(row: QueueRow): number {
    if (row.progress?.percent !== null && row.progress?.percent !== undefined) {
      return clamp(row.progress.percent, 0, 100);
    }
    if (isDone(row.job.state)) {
      return 100;
    }
    const downloaded = row.progress?.downloadedBytes;
    const total = row.progress?.totalBytes ?? row.job.fileSizeBytes;
    if (downloaded && total && total > 0) {
      return clamp((downloaded / total) * 100, 0, 100);
    }
    return 0;
  }

  function clamp(value: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, value));
  }

  function formatPercent(row: QueueRow): string {
    const percent = percentFor(row);
    return percent > 0 && percent < 1 ? '<1%' : `${Math.round(percent)}%`;
  }

  function formatOptionalBytes(bytes: number | null | undefined): string {
    return bytes === null || bytes === undefined ? '-' : formatBytes(bytes);
  }

  function formatByteProgress(progress: ProgressFrame | undefined, job: DownloadQueueJob): string {
    const downloaded = progress?.downloadedBytes;
    const total = progress?.totalBytes ?? job.fileSizeBytes;
    if (downloaded !== null && downloaded !== undefined && total !== null && total !== undefined) {
      return `${formatBytes(downloaded)} / ${formatBytes(total)}`;
    }
    if (total !== null && total !== undefined) {
      return `0 B / ${formatBytes(total)}`;
    }
    return '-';
  }

  function formatSpeed(speed: string | null | undefined): string {
    return speed?.trim() || '-';
  }

  function formatElapsed(job: DownloadQueueJob): string {
    const started = Date.parse(job.createdAt);
    const ended = terminalEndedAt(job);
    if (Number.isNaN(started) || Number.isNaN(ended) || ended < started) {
      return '-';
    }
    return formatDurationMs(ended - started);
  }

  function terminalEndedAt(job: DownloadQueueJob): number {
    if (job.completedAt) {
      return Date.parse(job.completedAt);
    }
    if (isDone(job.state) || isCancelled(job.state) || isFailed(job.state)) {
      return Date.parse(job.updatedAt);
    }
    return now;
  }

  function formatEta(seconds: number | null | undefined): string {
    return seconds === null || seconds === undefined ? '-' : formatDurationMs(seconds * 1000);
  }

  function formatDurationMs(ms: number): string {
    const totalSeconds = Math.max(0, Math.floor(ms / 1000));
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    if (hours > 0) {
      return `${hours}h ${minutes}m`;
    }
    if (minutes > 0) {
      return `${minutes}m ${seconds}s`;
    }
    return `${seconds}s`;
  }

  function providerFor(sourceUrl: string): string {
    try {
      return new URL(sourceUrl).hostname.replace(/^www\./, '');
    } catch {
      return 'unknown source';
    }
  }

  function displayTitle(sourceUrl: string): string {
    try {
      const url = new URL(sourceUrl);
      const path = decodeURIComponent(url.pathname.split('/').filter(Boolean).at(-1) ?? '');
      return path || url.hostname.replace(/^www\./, '') || sourceUrl;
    } catch {
      return sourceUrl;
    }
  }

  function compactUrl(sourceUrl: string): string {
    try {
      const url = new URL(sourceUrl);
      return `${url.hostname.replace(/^www\./, '')}${url.pathname === '/' ? '' : url.pathname}`;
    } catch {
      return sourceUrl;
    }
  }

  function stateTone(state: string): string {
    if (isDone(state)) {
      return 'bg-emerald-500/12 text-emerald-300 ring-emerald-500/20';
    }
    if (isFailed(state)) {
      return 'bg-red-500/12 text-red-300 ring-red-500/25';
    }
    if (isCancelled(state)) {
      return 'bg-slate-500/12 text-slate-300 ring-slate-500/20';
    }
    if (isQueued(state)) {
      return 'bg-slate-700/50 text-slate-300 ring-slate-600/40';
    }
    return 'bg-blue-500/12 text-blue-300 ring-blue-500/20';
  }

  function rowTone(state: string): string {
    if (isFailed(state)) {
      return 'border-red-500/45 bg-red-950/10';
    }
    if (isActive(state)) {
      return 'border-blue-500/60 bg-blue-950/10';
    }
    return 'border-slate-800/90 bg-slate-900/45';
  }

  function sourceInitial(provider: string): string {
    return provider.slice(0, 1).toUpperCase();
  }

  function displayState(row: QueueRow): string {
    const state = row.job.state;
    if (normalizeState(state) === 'downloadpending' && hasActiveDownloadProgress(row.progress)) {
      return row.progress?.phase?.trim() || 'Downloading';
    }
    return state;
  }

  function hasActiveDownloadProgress(progress: ProgressFrame | undefined): boolean {
    if (!progress) {
      return false;
    }
    const phase = progress.phase.trim().toLowerCase();
    return phase === 'downloading' || progress.percent !== null || progress.downloadedBytes !== null;
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
        Download queue · {activeCount} active · {queuedCount} queued · {failedCount} failed
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

  <div class="mt-6 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
    <div class="flex gap-2 overflow-x-auto pb-1" aria-label="Job filters">
      {#each tabs as tab}
        <button
          type="button"
          onclick={() => (activeFilter = tab.key)}
          class={[
            'inline-flex h-9 shrink-0 items-center gap-2 rounded-full px-4 text-xs font-semibold transition',
            activeFilter === tab.key
              ? 'bg-slate-100 text-slate-950'
              : 'bg-slate-800/75 text-slate-300 hover:bg-slate-700'
          ]}
          aria-current={activeFilter === tab.key ? 'page' : undefined}
        >
          {tab.label}
          <span
            class={[
              'rounded-full px-1.5 py-0.5 text-[10px]',
              activeFilter === tab.key ? 'bg-slate-300 text-slate-800' : 'bg-slate-950/50 text-slate-500'
            ]}
          >
            {tab.count}
          </span>
        </button>
      {/each}
    </div>

    <Input
      type="search"
      bind:value={query}
      aria-label="Search jobs"
      placeholder="Search source, job id, storage..."
      class="h-10 w-full border-slate-800! bg-slate-900/80! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500! lg:w-80"
    />
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
  {:else if filteredRows.length === 0}
    <div class="mt-8 rounded-xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <ServerOutline class="mx-auto h-10 w-10 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No jobs match this view</p>
      <p class="mt-1 text-sm text-slate-500">Queue a download or change the filters.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each filteredRows as row (row.job.jobId)}
        {@const job = row.job}
        {@const provider = providerFor(job.sourceUrl)}
        {@const percent = percentFor(row)}
        <article class={['rounded-xl border p-4 shadow-lg shadow-black/10 transition', rowTone(job.state)]}>
          <div class="grid gap-4 xl:grid-cols-[minmax(22rem,1.8fr)_8rem_9rem_9rem_8rem_9rem_minmax(17rem,auto)] xl:items-center">
            <div class="flex min-w-0 items-start gap-3">
              <span
                class="mt-0.5 grid h-10 w-10 shrink-0 place-items-center rounded-lg bg-slate-800 text-sm font-bold text-blue-300 ring-1 ring-slate-700"
                aria-hidden="true"
              >
                {sourceInitial(provider)}
              </span>
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                  <h2 class="min-w-0 truncate text-sm font-semibold text-slate-100">
                    {displayTitle(job.sourceUrl)}
                  </h2>
                  <span
                    class={['rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', stateTone(job.state)]}
                  >
                    {displayState(row)}
                  </span>
                </div>
                <p class="mt-1 truncate text-xs text-slate-500">
                  {provider} · {compactUrl(job.sourceUrl)}
                </p>
                <div class="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-xs text-slate-500">
                  <span class="inline-flex min-w-0 items-center gap-1">
                    <span class="shrink-0 text-slate-600">Job ID</span>
                    <span class="break-all font-mono text-slate-400">{job.jobId}</span>
                  </span>
                  <span class="inline-flex items-center gap-1">
                    <ServerOutline class="h-3.5 w-3.5 text-slate-600" />
                    {job.storageKey ?? 'default'}
                  </span>
                  <span>{job.sourceKind}</span>
                  <span>priority {job.priority}</span>
                </div>
                {#if job.failureMessage}
                  <p class="mt-2 line-clamp-1 text-xs text-red-300">
                    {job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}
                  </p>
                {:else if row.progress?.message}
                  <p class="mt-2 line-clamp-1 text-xs text-slate-500">{row.progress.message}</p>
                {/if}
              </div>
            </div>

            <div>
              <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600 xl:hidden">Size</p>
              <p class="mt-1 text-sm font-medium text-slate-300 xl:mt-0">
                {formatOptionalBytes(row.progress?.totalBytes ?? job.fileSizeBytes)}
              </p>
            </div>

            <div>
              <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600 xl:hidden">Progress</p>
              <div class="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-slate-800 xl:mt-0">
                <div
                  class={['h-full rounded-full', isFailed(job.state) ? 'bg-red-400' : 'bg-blue-500']}
                  style={`width: ${percent}%`}
                ></div>
              </div>
              <p class="mt-1 text-xs text-slate-400">{formatPercent(row)}</p>
            </div>

            <div>
              <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600 xl:hidden">Bytes</p>
              <p class="mt-1 text-sm font-medium text-slate-300 xl:mt-0">
                {formatByteProgress(row.progress, job)}
              </p>
            </div>

            <div>
              <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600 xl:hidden">Speed</p>
              <p class="mt-1 text-sm font-medium text-slate-300 xl:mt-0">{formatSpeed(row.progress?.speed)}</p>
              {#if row.progress?.etaSeconds !== null && row.progress?.etaSeconds !== undefined}
                <p class="mt-0.5 text-xs text-slate-600">eta {formatEta(row.progress.etaSeconds)}</p>
              {/if}
            </div>

            <div>
              <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600 xl:hidden">Working</p>
              <p class="mt-1 inline-flex items-center gap-1.5 text-sm font-medium text-slate-300 xl:mt-0">
                <ClockOutline class="h-3.5 w-3.5 text-slate-600" />
                {formatElapsed(job)}
              </p>
            </div>

            <div class="flex flex-wrap items-center gap-2 xl:justify-end">
              {#if isDone(job.state)}
                <span class="inline-flex h-11 min-w-28 items-center justify-center gap-2 rounded-lg border border-emerald-500/20 bg-emerald-500/10 px-3 text-sm font-semibold text-emerald-300">
                  <CheckCircleOutline class="h-4 w-4" />
                  Done
                </span>
              {/if}
              {#if canUpdatePriority(job)}
                <button
                  type="button"
                  class="inline-flex h-11 min-w-28 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-sm font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-40"
                  title="Set priority"
                  aria-label="Set priority"
                  disabled={Boolean(actionBusy[job.jobId])}
                  onclick={() => updatePriority(row)}
                >
                  {#if actionBusy[job.jobId] === 'priority'}
                    <Spinner size="4" />
                  {:else}
                    <FireOutline class="h-4 w-4" />
                  {/if}
                  Priority
                </button>
              {/if}
              {#if canRestart(job)}
                <button
                  type="button"
                  class="inline-flex h-11 min-w-28 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-sm font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-slate-800 hover:text-white disabled:opacity-40"
                  title="Restart job"
                  aria-label="Restart job"
                  disabled={Boolean(actionBusy[job.jobId])}
                  onclick={() => restartDownload(row)}
                >
                  {#if actionBusy[job.jobId] === 'restart'}
                    <Spinner size="4" />
                  {:else}
                    <ArrowsRepeatOutline class="h-4 w-4" />
                  {/if}
                  Restart
                </button>
              {/if}
              {#if canCancel(job)}
                <button
                  type="button"
                  class="inline-flex h-11 min-w-28 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-sm font-semibold text-slate-200 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-40"
                  title="Cancel job"
                  aria-label="Cancel job"
                  disabled={Boolean(actionBusy[job.jobId])}
                  onclick={() => cancelDownload(row)}
                >
                  {#if actionBusy[job.jobId] === 'cancel'}
                    <Spinner size="4" />
                  {:else}
                    <StopOutline class="h-4 w-4" />
                  {/if}
                  Cancel
                </button>
              {/if}
              <a
                href={job.sourceUrl}
                target="_blank"
                rel="noreferrer"
                class="inline-flex h-11 min-w-32 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-sm font-semibold text-slate-200 transition hover:border-slate-500 hover:bg-slate-800 hover:text-white"
                title="View source"
                aria-label="View source"
              >
                <ArrowUpRightFromSquareOutline class="h-4 w-4" />
                View Source
              </a>
            </div>
          </div>
        </article>
      {/each}
    </div>
  {/if}
</section>
