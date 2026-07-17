<script lang="ts">
  import { Spinner } from 'flowbite-svelte';
  import {
    ArrowsRepeatOutline,
    ArrowUpRightFromSquareOutline,
    ChevronDownOutline,
    ExclamationCircleOutline,
    FireOutline,
    PlayOutline,
    StopOutline
  } from 'flowbite-svelte-icons';
  import {
    fetchJobHistory,
    fetchJobMediaGuid,
    type DownloadQueueHistoryEntry,
    type DownloadQueueJob,
    type ProgressFrame
  } from '$lib/api/downloadQueue';
  import type { QueueRow } from '$lib/stores/downloadQueue';
  import { formatOptionalBytes, isActive, isCancelled, isDone, isFailed, isQueued, normalizeState } from '$lib/jobs/jobState';

  let {
    row,
    now,
    optionPresetsByKey,
    busyAction,
    oncancel,
    onrestart,
    onpriority
  }: {
    row: QueueRow;
    now: number;
    optionPresetsByKey: Map<string, string>;
    busyAction: string | undefined;
    oncancel: (row: QueueRow) => void;
    onrestart: (row: QueueRow) => void;
    onpriority: (row: QueueRow) => void;
  } = $props();

  let expanded = $state(false);
  let history = $state<DownloadQueueHistoryEntry[] | 'loading' | 'error' | undefined>(undefined);
  let mediaGuid = $state<string | null | undefined>(undefined);
  let liveMessages = $state<{ text: string; at: number }[]>([]);

  const job = $derived(row.job);
  const provider = $derived(providerFor(job.sourceUrl));
  const percent = $derived(percentFor(row));
  const showProgressDetails = $derived(isActive(job.state));

  $effect(() => {
    if (isDone(job.state) && mediaGuid === undefined) {
      void loadMediaGuid(job.jobId);
    }
  });

  $effect(() => {
    const message = row.progress?.message?.trim();
    if (message && liveMessages.at(-1)?.text !== message) {
      liveMessages = [...liveMessages, { text: message, at: Date.now() }].slice(-50);
    }
  });

  async function loadMediaGuid(jobId: string): Promise<void> {
    try {
      mediaGuid = await fetchJobMediaGuid(jobId);
    } catch {
      mediaGuid = null;
    }
  }

  async function toggleExpanded(): Promise<void> {
    expanded = !expanded;
    if (!expanded) {
      return;
    }
    history = 'loading';
    try {
      history = await fetchJobHistory(job.jobId);
      // The fresh history now durably includes any progress lines the backend already
      // persisted, so drop the ephemeral live buffer to avoid showing lines twice.
      liveMessages = [];
    } catch {
      history = 'error';
    }
  }

  function stop(event: Event): void {
    event.stopPropagation();
  }

  function canCancel(j: DownloadQueueJob): boolean {
    // FailedTransient is a special case: the backend allows force-cancelling it even though it's
    // otherwise a terminal failure, because a known race can leave a yt-dlp process still running
    // on the worker (e.g. stuck retrying a rate-limited subtitle fetch) after the job already
    // recorded FailedTransient. Other failed states (FailedPermanent/DeadLettered/ProviderHalted)
    // are not affected by that race and stay non-cancellable.
    if (normalizeState(j.state) === 'failedtransient') {
      return true;
    }
    return !isDone(j.state) && !isCancelled(j.state) && !isFailed(j.state);
  }

  function canRestart(j: DownloadQueueJob): boolean {
    return normalizeState(j.state) === 'providerhalted' || isCancelled(j.state);
  }

  function canUpdatePriority(j: DownloadQueueJob): boolean {
    return isQueued(j.state);
  }

  function percentFor(r: QueueRow): number {
    if (r.progress?.percent !== null && r.progress?.percent !== undefined) {
      return clamp(r.progress.percent, 0, 100);
    }
    if (isDone(r.job.state)) {
      return 100;
    }
    const downloaded = r.progress?.downloadedBytes;
    const total = r.progress?.totalBytes ?? r.job.fileSizeBytes;
    if (downloaded && total && total > 0) {
      return clamp((downloaded / total) * 100, 0, 100);
    }
    return 0;
  }

  function clamp(value: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, value));
  }

  function formatPercent(r: QueueRow): string {
    const p = percentFor(r);
    return p > 0 && p < 1 ? '<1%' : `${Math.round(p)}%`;
  }

  function formatByteProgress(progress: ProgressFrame | undefined, j: DownloadQueueJob): string {
    const downloaded = progress?.downloadedBytes;
    const total = progress?.totalBytes ?? j.fileSizeBytes;
    if (downloaded !== null && downloaded !== undefined && total !== null && total !== undefined) {
      return `${formatOptionalBytes(downloaded)} / ${formatOptionalBytes(total)}`;
    }
    if (total !== null && total !== undefined) {
      return `0 B / ${formatOptionalBytes(total)}`;
    }
    return '-';
  }

  function formatSpeed(speed: string | null | undefined): string {
    return speed?.trim() || '-';
  }

  function formatElapsed(j: DownloadQueueJob): string {
    const started = Date.parse(j.createdAt);
    const ended = terminalEndedAt(j);
    if (Number.isNaN(started) || Number.isNaN(ended) || ended < started) {
      return '-';
    }
    return formatDurationMs(ended - started);
  }

  function terminalEndedAt(j: DownloadQueueJob): number {
    if (j.completedAt) {
      return Date.parse(j.completedAt);
    }
    if (isDone(j.state) || isCancelled(j.state) || isFailed(j.state)) {
      return Date.parse(j.updatedAt);
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

  function barColor(state: string): string {
    if (isDone(state)) {
      return 'bg-emerald-500';
    }
    if (isFailed(state)) {
      return 'bg-red-400';
    }
    if (isCancelled(state)) {
      return 'bg-slate-500';
    }
    return 'bg-blue-500';
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

  function sourceInitial(p: string): string {
    return p.slice(0, 1).toUpperCase();
  }

  function originBadge(sourceKind: string): { label: string; tone: string } | null {
    switch (sourceKind.toLowerCase()) {
      case 'playlist':
        return { label: 'PLAYLIST', tone: 'bg-violet-500/12 text-violet-300 ring-violet-500/25' };
      case 'channel':
        return { label: 'CHANNEL', tone: 'bg-cyan-500/12 text-cyan-300 ring-cyan-500/25' };
      default:
        return null;
    }
  }

  function displayState(r: QueueRow): string {
    const state = r.job.state;
    if (normalizeState(state) === 'downloadpending' && hasActiveDownloadProgress(r.progress)) {
      return r.progress?.phase?.trim() || 'Downloading';
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

  function findHistoryEntry(entries: DownloadQueueHistoryEntry[], eventName: string): DownloadQueueHistoryEntry | undefined {
    return entries.find((entry) => entry.eventName === eventName);
  }

  function optionSetLabel(entries: DownloadQueueHistoryEntry[] | 'loading' | 'error' | undefined): string {
    if (entries === undefined || entries === 'loading') {
      return 'Loading…';
    }
    if (entries === 'error') {
      return 'Unavailable';
    }
    const requested = findHistoryEntry(entries, 'DownloadRequested');
    if (!requested?.payloadJson) {
      return 'Unknown';
    }
    try {
      const payload = JSON.parse(requested.payloadJson) as { presetKey?: string | null };
      if (!payload.presetKey) {
        return 'Custom options';
      }
      return optionPresetsByKey.get(payload.presetKey) ?? payload.presetKey;
    } catch {
      return 'Unknown';
    }
  }

  function formatLogTime(recordedAt: string): string {
    const parsed = new Date(recordedAt);
    return Number.isNaN(parsed.getTime()) ? recordedAt : parsed.toLocaleTimeString([], { hour12: false });
  }
</script>

<article class={['rounded-xl border p-4 shadow-lg shadow-black/10 transition', rowTone(job.state)]}>
  <div
    class="grid cursor-pointer gap-3 md:grid-cols-[minmax(0,1fr)_18rem_8.5rem] md:items-center"
    role="button"
    tabindex="0"
    aria-expanded={expanded}
    onclick={toggleExpanded}
    onkeydown={(event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        void toggleExpanded();
      }
    }}
  >
    <div class="flex min-w-0 items-start gap-3">
      <span
        class="mt-0.5 grid h-10 w-10 shrink-0 place-items-center rounded-lg bg-slate-800 text-sm font-bold text-blue-300 ring-1 ring-slate-700"
        aria-hidden="true"
      >
        {sourceInitial(provider)}
      </span>
      <div class="min-w-0">
        <div class="flex min-w-0 items-center gap-2">
          <ChevronDownOutline class={['h-3.5 w-3.5 shrink-0 text-slate-600 transition-transform', expanded ? 'rotate-180' : '']} />
          <h2 class="min-w-0 truncate text-sm font-semibold text-slate-100">
            {displayTitle(job.sourceUrl)}
          </h2>
          <span class={['shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', stateTone(job.state)]}>
            {displayState(row)}
          </span>
          {#if originBadge(job.sourceKind)}
            {@const origin = originBadge(job.sourceKind)!}
            <span class={['shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', origin.tone]}>
              {origin.label}
            </span>
          {/if}
        </div>
        <p class="mt-1 truncate text-xs text-slate-500">
          {provider} · {job.storageKey ?? 'default'} · {formatOptionalBytes(row.progress?.totalBytes ?? job.fileSizeBytes)}
        </p>
        {#if job.failureMessage}
          <p class="mt-2 line-clamp-1 text-xs text-red-300">
            {job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}
          </p>
        {/if}
      </div>
    </div>

    <div class="flex items-center gap-3">
      <div class="min-w-0 flex-1">
        <div class="h-1.5 w-full overflow-hidden rounded-full bg-slate-800">
          <div class={['h-full rounded-full', barColor(job.state)]} style={`width: ${percent}%`}></div>
        </div>
        {#if showProgressDetails}
          <p class="mt-1 text-xs text-slate-500">
            {formatPercent(row)} · {formatSpeed(row.progress?.speed)} · {formatElapsed(job)}
          </p>
        {/if}
      </div>
      {#if showProgressDetails}
        <div class="w-20 shrink-0 text-right">
          <p class="text-xs font-medium text-slate-300">eta {formatEta(row.progress?.etaSeconds)}</p>
          <p class="mt-0.5 text-[11px] text-slate-500">{formatByteProgress(row.progress, job)}</p>
        </div>
      {/if}
    </div>

    <div class="flex flex-wrap items-center justify-end gap-1.5">
      {#if isDone(job.state) && mediaGuid}
        <a
          href={`/watch/${mediaGuid}`}
          onclick={stop}
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 text-slate-200 transition hover:border-emerald-500/60 hover:bg-emerald-500/10 hover:text-emerald-200"
          title="Watch"
          aria-label="Watch"
        >
          <PlayOutline class="h-4 w-4" />
        </a>
      {/if}
      {#if canUpdatePriority(job)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-40"
          title="Set priority"
          aria-label="Set priority"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            onpriority(row);
          }}
        >
          {#if busyAction === 'priority'}
            <Spinner size="4" />
          {:else}
            <FireOutline class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      {#if canRestart(job)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 text-slate-200 transition hover:border-blue-500/60 hover:bg-slate-800 hover:text-white disabled:opacity-40"
          title="Restart job"
          aria-label="Restart job"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            onrestart(row);
          }}
        >
          {#if busyAction === 'restart'}
            <Spinner size="4" />
          {:else}
            <ArrowsRepeatOutline class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      {#if canCancel(job)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 text-slate-200 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-40"
          title="Cancel job"
          aria-label="Cancel job"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            oncancel(row);
          }}
        >
          {#if busyAction === 'cancel'}
            <Spinner size="4" />
          {:else}
            <StopOutline class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      <a
        href={job.sourceUrl}
        target="_blank"
        rel="noreferrer"
        onclick={stop}
        class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 text-slate-200 transition hover:border-slate-500 hover:bg-slate-800 hover:text-white"
        title="View source"
        aria-label="View source"
      >
        <ArrowUpRightFromSquareOutline class="h-4 w-4" />
      </a>
    </div>
  </div>

  {#if expanded}
    <div class="mt-3 border-t border-slate-800/70 pt-3">
      <div class="flex flex-wrap gap-x-4 gap-y-1.5 text-xs text-slate-500">
        <span class="inline-flex min-w-0 items-center gap-1">
          <span class="shrink-0 text-slate-600">Job ID</span>
          <span class="break-all font-mono text-slate-400">{job.jobId}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-slate-600">Option set</span>
          <span class="text-slate-400">{optionSetLabel(history)}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-slate-600">Priority</span>
          <span class="text-slate-400">{job.priority}</span>
        </span>
      </div>

      <div class="mt-3 max-h-48 overflow-y-auto rounded-lg border border-slate-800/80 bg-slate-950/60 p-3 font-mono text-xs">
        {#if job.failureMessage}
          <p class="flex items-start gap-1.5 whitespace-pre-wrap break-words text-red-300">
            <ExclamationCircleOutline class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            {job.failureKind ? `[${job.failureKind}] ` : ''}{job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}
          </p>
        {/if}
        {#if history === 'loading'}
          <p class="text-slate-600">Loading history…</p>
        {:else if history === 'error'}
          <p class="text-red-400">Could not load job history.</p>
        {:else if history === undefined}
          <p class="text-slate-600">-</p>
        {:else if history.length === 0}
          <p class="text-slate-600">No recorded events.</p>
        {:else}
          {#each history as entry (entry.id)}
            <p class="whitespace-pre-wrap break-words text-slate-400">
              <span class="text-slate-600">[{formatLogTime(entry.recordedAt)}]</span>
              {entry.eventName === 'ProgressLine' ? entry.payloadJson : entry.eventName}
            </p>
          {/each}
        {/if}
        {#each liveMessages as entry (entry.at)}
          <p class="whitespace-pre-wrap break-words text-slate-500">{entry.text}</p>
        {/each}
      </div>
    </div>
  {/if}
</article>
