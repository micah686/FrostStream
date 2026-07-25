<script lang="ts">
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
  import {
    canStart,
    canStop,
    formatOptionalBytes,
    humanizeDownloadName,
    isActive,
    isDone,
    isFailed,
    isQueued,
    isStopped,
    isTerminal,
    normalizeStatus
  } from '$lib/jobs/jobState';

  let {
    row,
    now,
    optionPresetsByKey,
    busyAction,
    onstop,
    onstart,
    onclearprovider,
    ongroupstop,
    ongroupstart,
    onpriority
  }: {
    row: QueueRow;
    now: number;
    optionPresetsByKey: Map<string, string>;
    busyAction: string | undefined;
    onstop: (row: QueueRow) => void;
    onstart: (row: QueueRow) => void;
    onclearprovider: (row: QueueRow) => void;
    ongroupstop: (row: QueueRow) => void;
    ongroupstart: (row: QueueRow) => void;
    onpriority: (row: QueueRow) => void;
  } = $props();

  let expanded = $state(false);
  let history = $state<DownloadQueueHistoryEntry[] | 'loading' | 'error' | undefined>(undefined);
  let mediaGuid = $state<string | null | undefined>(undefined);
  let liveMessages = $state<{ text: string; at: number }[]>([]);

  const job = $derived(row.job);
  const provider = $derived(providerFor(job.sourceUrl));
  const percent = $derived(percentFor(row));
  const showProgressDetails = $derived(isActive(job.status));
  let previousStatus = $state<string | undefined>(undefined);

  $effect(() => {
    if (isDone(job.status) && mediaGuid === undefined) {
      void loadMediaGuid(job.jobId);
    }
  });

  $effect(() => {
    const message = row.progress?.message?.trim();
    if (message && liveMessages.at(-1)?.text !== message) {
      liveMessages = [...liveMessages, { text: message, at: Date.now() }].slice(-50);
    }
  });

  $effect(() => {
    const status = normalizeStatus(job.status);
    const enteredTerminal = previousStatus !== undefined && status !== previousStatus && isTerminal(status);
    previousStatus = status;
    if (enteredTerminal && expanded) {
      void refreshHistory(job.jobId);
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
    await refreshHistory(job.jobId);
  }

  async function refreshHistory(jobId: string): Promise<void> {
    history = 'loading';
    try {
      history = await fetchJobHistory(jobId);
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

  function canUpdatePriority(j: DownloadQueueJob): boolean {
    return isQueued(j.status);
  }

  function percentFor(r: QueueRow): number {
    if (r.progress?.percent !== null && r.progress?.percent !== undefined) {
      return clamp(r.progress.percent, 0, 100);
    }
    if (isDone(r.job.status)) {
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
    if (isTerminal(j.status)) {
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

  function statusTone(status: string): string {
    if (normalizeStatus(status) === 'completedwithwarnings') {
      return 'bg-warning/12 text-warning ring-warning/25';
    }
    if (isDone(status)) {
      return 'bg-success/12 text-success ring-success/20';
    }
    if (isFailed(status)) {
      return 'bg-error/12 text-error ring-error/25';
    }
    if (isStopped(status)) {
      return 'bg-base-200/12 text-base-content/80 ring-base-300/20';
    }
    if (isQueued(status)) {
      return 'bg-base-300/50 text-base-content/80 ring-base-content/30';
    }
    return 'bg-primary/12 text-primary ring-primary/20';
  }

  function barColor(status: string): string {
    if (normalizeStatus(status) === 'completedwithwarnings') {
      return 'bg-warning';
    }
    if (isDone(status)) {
      return 'bg-success';
    }
    if (isFailed(status)) {
      return 'bg-error';
    }
    if (isStopped(status)) {
      return 'bg-base-200';
    }
    return 'bg-primary';
  }

  function rowTone(status: string): string {
    if (isFailed(status)) {
      return 'border-error/45 bg-error/10';
    }
    if (isActive(status)) {
      return 'border-primary/60 bg-primary/10';
    }
    return 'border-base-300/90 bg-base-200/45';
  }

  function sourceInitial(p: string): string {
    return p.slice(0, 1).toUpperCase();
  }

  function originBadge(sourceKind: string): { label: string; tone: string } | null {
    switch (sourceKind.toLowerCase()) {
      case 'playlist':
        return { label: 'PLAYLIST', tone: 'bg-secondary/12 text-secondary ring-secondary/25' };
      case 'channel':
        return { label: 'CHANNEL', tone: 'bg-accent/12 text-accent ring-accent/25' };
      default:
        return null;
    }
  }

  function isCollectionJob(j: DownloadQueueJob): boolean {
    const kind = j.sourceKind.toLowerCase();
    return kind === 'playlist' || kind === 'channel';
  }

  function shortCollectionId(id: string): string {
    return id.split('-')[0] ?? id.slice(0, 8);
  }

  function displayStatus(r: QueueRow): string {
    if (normalizeStatus(r.job.status) === 'running' && hasActiveDownloadProgress(r.progress)) {
      return r.progress?.phase?.trim() || r.job.status;
    }
    return humanizeDownloadName(r.job.status);
  }

  function displayStage(r: QueueRow): string {
    const stage = humanizeDownloadName(r.job.stage);
    const state = humanizeDownloadName(r.job.stageStatus);
    if (normalizeStatus(r.job.stageStatus) === 'retrywaiting') {
      return `${stage} · retry ${Math.min(r.job.attempt + 1, r.job.maxAttempts)}/${r.job.maxAttempts}`;
    }
    return `${stage} · ${state}`;
  }

  function hasActiveDownloadProgress(progress: ProgressFrame | undefined): boolean {
    if (!progress) {
      return false;
    }
    const phase = progress.phase.trim().toLowerCase();
    return (
      phase === 'downloading' ||
      phase === 'optional sidecar warning' ||
      progress.percent !== null ||
      progress.downloadedBytes !== null
    );
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

<article class={['rounded-xl border p-4 shadow-lg shadow-black/10 transition', rowTone(job.status)]}>
  <div
    class="grid cursor-pointer gap-3 md:grid-cols-[minmax(0,1fr)_18rem_minmax(8.5rem,auto)] md:items-center"
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
        class="mt-0.5 grid h-10 w-10 shrink-0 place-items-center rounded-lg bg-base-300 text-sm font-bold text-primary ring-1 ring-base-content/20"
        aria-hidden="true"
      >
        {sourceInitial(provider)}
      </span>
      <div class="min-w-0">
        <div class="flex min-w-0 items-center gap-2">
          <ChevronDownOutline class={['h-3.5 w-3.5 shrink-0 text-base-content/40 transition-transform', expanded ? 'rotate-180' : '']} />
          <h2 class="min-w-0 truncate text-sm font-semibold text-base-content">
            {displayTitle(job.sourceUrl)}
          </h2>
          <span class={['shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', statusTone(job.status)]}>
            {displayStatus(row)}
          </span>
          {#if originBadge(job.sourceKind)}
            {@const origin = originBadge(job.sourceKind)!}
            <span class={['shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', origin.tone]}>
              {origin.label}
            </span>
          {/if}
        </div>
        <p class="mt-1 truncate text-xs text-base-content/50">
          {provider} · {job.storageKey ?? 'default'} · {formatOptionalBytes(row.progress?.totalBytes ?? job.fileSizeBytes)}
          {#if isCollectionJob(job)}
            · group <span class="font-mono">{shortCollectionId(job.correlationId)}</span>
          {/if}
        </p>
        {#if isActive(job.status) || isQueued(job.status) || isFailed(job.status) || isStopped(job.status)}
          <p class="mt-1 truncate text-[11px] text-base-content/50">
            {displayStage(row)}
            {#if job.artifactKey}
              · <span class="font-mono">{job.artifactKey}</span>
            {/if}
          </p>
        {/if}
        {#if job.failureMessage}
          <p class="mt-2 line-clamp-1 text-xs text-error">
            {job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}
          </p>
        {/if}
      </div>
    </div>

    <div class="flex items-center gap-3">
      <div class="min-w-0 flex-1">
        <div class="h-1.5 w-full overflow-hidden rounded-full bg-base-300">
          <div class={['h-full rounded-full', barColor(job.status)]} style={`width: ${percent}%`}></div>
        </div>
        {#if showProgressDetails}
          <p class="mt-1 text-xs text-base-content/50">
            {formatPercent(row)} · {formatSpeed(row.progress?.speed)} · {formatElapsed(job)}
          </p>
        {/if}
      </div>
      {#if showProgressDetails}
        <div class="w-20 shrink-0 text-right">
          <p class="text-xs font-medium text-base-content/80">eta {formatEta(row.progress?.etaSeconds)}</p>
          <p class="mt-0.5 text-[11px] text-base-content/50">{formatByteProgress(row.progress, job)}</p>
        </div>
      {/if}
    </div>

    <div class="flex flex-wrap items-center justify-end gap-1.5">
      {#if isDone(job.status) && mediaGuid}
        <a
          href={`/watch/${mediaGuid}`}
          onclick={stop}
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 text-base-content/90 transition hover:border-success/60 hover:bg-success/10 hover:text-success"
          title="Watch"
          aria-label="Watch"
        >
          <PlayOutline class="h-4 w-4" />
        </a>
      {/if}
      {#if canUpdatePriority(job)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary disabled:opacity-40"
          title="Set priority"
          aria-label="Set priority"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            onpriority(row);
          }}
        >
          {#if busyAction === 'priority'}
            <span class="loading loading-spinner loading-xs"></span>
          {:else}
            <FireOutline class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      {#if canStart(job.status)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 text-base-content/90 transition hover:border-primary/60 hover:bg-base-300 hover:text-base-content disabled:opacity-40"
          title="Start a new run"
          aria-label="Start a new run"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            onstart(row);
          }}
        >
          {#if busyAction === 'start'}
            <span class="loading loading-spinner loading-xs"></span>
          {:else}
            <PlayOutline class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      {#if job.failureCode === 'provider_circuit_open' || job.failureKind?.toLowerCase() === 'providerblocked'}
        <button
          type="button"
          class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-warning/35 bg-warning/10 px-2 text-[11px] font-semibold text-warning transition hover:border-warning/70 hover:bg-warning/20 disabled:opacity-40"
          title="Clear the provider block; this job will still require Start"
          aria-label="Clear provider block"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            onclearprovider(row);
          }}
        >
          {#if busyAction === 'clear-provider'}
            <span class="loading loading-spinner loading-xs"></span>
          {:else}
            <ArrowsRepeatOutline class="h-3.5 w-3.5" />
          {/if}
          Clear block
        </button>
      {/if}
      {#if canStop(job.status)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 text-base-content/90 transition hover:border-error/60 hover:bg-error/10 hover:text-error disabled:opacity-40"
          title="Stop job"
          aria-label="Stop job"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            onstop(row);
          }}
        >
          {#if busyAction === 'stop'}
            <span class="loading loading-spinner loading-xs"></span>
          {:else}
            <StopOutline class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      {#if isCollectionJob(job) && canStart(job.status)}
        <button
          type="button"
          class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-2 text-[11px] font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary disabled:opacity-40"
          title="Start every failed or stopped job in this group"
          aria-label="Start group"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            ongroupstart(row);
          }}
        >
          {#if busyAction === 'group-start'}
            <span class="loading loading-spinner loading-xs"></span>
          {:else}
            <ArrowsRepeatOutline class="h-3.5 w-3.5" />
          {/if}
          Group
        </button>
      {/if}
      {#if isCollectionJob(job) && canStop(job.status)}
        <button
          type="button"
          class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-2 text-[11px] font-semibold text-base-content/90 transition hover:border-error/60 hover:bg-error/10 hover:text-error disabled:opacity-40"
          title="Stop every queued or running job in this group"
          aria-label="Stop group"
          disabled={Boolean(busyAction)}
          onclick={(event) => {
            stop(event);
            ongroupstop(row);
          }}
        >
          {#if busyAction === 'group-stop'}
            <span class="loading loading-spinner loading-xs"></span>
          {:else}
            <StopOutline class="h-3.5 w-3.5" />
          {/if}
          Group
        </button>
      {/if}
      <a
        href={job.sourceUrl}
        target="_blank"
        rel="noreferrer"
        onclick={stop}
        class="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 text-base-content/90 transition hover:border-base-300 hover:bg-base-300 hover:text-base-content"
        title="View source"
        aria-label="View source"
      >
        <ArrowUpRightFromSquareOutline class="h-4 w-4" />
      </a>
    </div>
  </div>

  {#if expanded}
    <div class="mt-3 border-t border-base-300/70 pt-3">
      <div class="flex flex-wrap gap-x-4 gap-y-1.5 text-xs text-base-content/50">
        <span class="inline-flex min-w-0 items-center gap-1">
          <span class="shrink-0 text-base-content/40">Job ID</span>
          <span class="break-all font-mono text-base-content/60">{job.jobId}</span>
        </span>
        {#if isCollectionJob(job)}
          <span class="inline-flex min-w-0 items-center gap-1">
            <span class="shrink-0 text-base-content/40">Collection ID</span>
            <span class="break-all font-mono text-base-content/60">{job.correlationId}</span>
          </span>
        {/if}
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Option set</span>
          <span class="text-base-content/60">{optionSetLabel(history)}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Run</span>
          <span class="text-base-content/60">#{job.runNumber}</span>
        </span>
        {#if job.runId}
          <span class="inline-flex min-w-0 items-center gap-1">
            <span class="shrink-0 text-base-content/40">Run ID</span>
            <span class="break-all font-mono text-base-content/60">{job.runId}</span>
          </span>
        {/if}
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Stage</span>
          <span class="text-base-content/60">{humanizeDownloadName(job.stage)} · {humanizeDownloadName(job.stageStatus)}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Attempt</span>
          <span class="text-base-content/60">{job.attempt || '-'} / {job.maxAttempts}</span>
        </span>
        {#if job.warningCount > 0}
          <span class="inline-flex items-center gap-1 text-warning">
            <span class="shrink-0 text-warning">Warnings</span>
            <span>{job.warningCount}</span>
          </span>
        {/if}
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Priority</span>
          <span class="text-base-content/60">{job.priority}</span>
        </span>
      </div>

      <div class="mt-3 max-h-48 overflow-y-auto rounded-lg border border-base-300/80 bg-base-200/60 p-3 font-mono text-xs">
        {#if job.failureMessage}
          <p class="flex items-start gap-1.5 whitespace-pre-wrap break-words text-error">
            <ExclamationCircleOutline class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            {job.failureKind ? `[${job.failureKind}] ` : ''}{job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}
          </p>
        {/if}
        {#if history === 'loading'}
          <p class="text-base-content/40">Loading history…</p>
        {:else if history === 'error'}
          <p class="text-error">Could not load job history.</p>
        {:else if history === undefined}
          <p class="text-base-content/40">-</p>
        {:else if history.length === 0}
          <p class="text-base-content/40">No recorded events.</p>
        {:else}
          {#each history as entry (entry.id)}
            <p class="whitespace-pre-wrap break-words text-base-content/60">
              <span class="text-base-content/40">[{formatLogTime(entry.recordedAt)}]</span>
              {entry.eventName === 'ProgressLine' ? entry.payloadJson : entry.eventName}
            </p>
          {/each}
        {/if}
        {#each liveMessages as entry (entry.at)}
          <p class="whitespace-pre-wrap break-words text-base-content/50">{entry.text}</p>
        {/each}
      </div>
    </div>
  {/if}
</article>
