<script lang="ts">
  import {
    Film,
    Folder,
    FileVideo,
    ListVideo,
    Tv,
    ListChecks,
    ChevronDown,
    CircleAlert,
    ExternalLink,
    Flame,
    Play,
    Repeat,
    Square
  } from '@lucide/svelte';
  import {
    fetchJobHistory,
    fetchJobMediaGuid,
    type DownloadQueueHistoryEntry,
    type DownloadQueueJob,
    type ProgressFrame
  } from '#lib/api/downloadQueue.js';
  import JobStages from './JobStages.svelte';
  import type { QueueRow } from '#lib/stores/downloadQueue.js';
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
  } from '#lib/jobs/jobState.js';

  let {
    row,
    optionPresetsByKey,
    busyAction,
    onstop,
    onstart,
    onclearprovider,
    onpriority,
    oncollection
  }: {
    row: QueueRow;
    optionPresetsByKey: Map<string, string>;
    busyAction: string | undefined;
    onstop: (row: QueueRow) => void;
    onstart: (row: QueueRow) => void;
    onclearprovider: (row: QueueRow) => void;
    onpriority: (row: QueueRow) => void;
    oncollection?: (id: string) => void;
  } = $props();

  let expanded = $state(false);
  let detailTab = $state('Stages');
  let history = $state<DownloadQueueHistoryEntry[] | 'loading' | 'error' | undefined>(undefined);
  let mediaGuid = $state<string | null | undefined>(undefined);
  let liveMessages = $state<{ text: string; at: number }[]>([]);
  let logEl = $state<HTMLDivElement | undefined>();
  let followLog = $state(true);

  const job = $derived(row.job);
  const provider = $derived(providerFor(job.sourceUrl));
  const percent = $derived(percentFor(row));
  let previousStatus = $state<string | undefined>(undefined);
  let previousRunKey = $state<string | undefined>(undefined);

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

  // A new run reuses the same job row, so the log has to be reset explicitly or it would
  // keep showing the previous run's history and live lines mixed in with the new ones.
  $effect(() => {
    const runKey = `${job.runId ?? ''}:${job.runNumber}`;
    if (previousRunKey !== undefined && runKey !== previousRunKey) {
      liveMessages = [];
      followLog = true;
      history = expanded ? 'loading' : undefined;
      if (expanded) {
        void refreshHistory(job.jobId);
      }
    }
    previousRunKey = runKey;
  });

  $effect(() => {
    void history;
    void liveMessages;
    if (followLog && logEl) {
      logEl.scrollTop = logEl.scrollHeight;
    }
  });

  function handleLogScroll(): void {
    if (!logEl) {
      return;
    }
    const distanceFromBottom = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight;
    followLog = distanceFromBottom < 24;
  }

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

  function downloadedBytesFor(r: QueueRow): number | null | undefined {
    if (!hasCurrentRunMetrics(r)) {
      return undefined;
    }
    const downloaded = r.progress?.downloadedBytes;
    if (downloaded !== null && downloaded !== undefined) {
      return downloaded;
    }
    // Progress frames are live-only, so a finished job reloaded from the API has none; its
    // downloaded total is by definition the whole file. Mirrors percentFor's isDone fallback.
    return isDone(r.job.status) ? (r.progress?.totalBytes ?? r.job.fileSizeBytes) : undefined;
  }

  function formatSpeed(speed: string | null | undefined): string {
    return speed?.trim() || '-';
  }

  function hasCurrentRunMetrics(r: QueueRow): boolean {
    return !isStopped(r.job.status) && (r.progress !== undefined || isDone(r.job.status));
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
    if (isDone(status)) {
      return 'badge-soft badge-success';
    }
    if (isFailed(status) || isStopped(status)) {
      return 'badge-soft badge-error';
    }
    return isActive(status) ? 'badge-soft badge-primary' : 'badge-ghost';
  }

  function isCollectionJob(j: DownloadQueueJob): boolean {
    const kind = j.sourceKind.toLowerCase();
    return kind === 'playlist' || kind === 'channel';
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

<article class="card overflow-hidden rounded-lg border border-base-300 bg-base-100">
  <div class="grid grid-cols-[2.25rem_minmax(0,1fr)_auto] gap-3 p-3.5 max-[470px]:grid-cols-[minmax(0,1fr)_auto]">
    <div class="flex h-8 items-center justify-center rounded bg-base-200 text-base-content/50 max-[470px]:hidden"><Film class="h-4 w-4" /></div>
    <div class="min-w-0">
      <h3 class="break-words text-sm font-semibold text-base-content">{job.title?.trim() || displayTitle(job.sourceUrl)}</h3>
      <div class="mt-1 text-xs text-base-content/55">
        <span>{provider}</span>
        {#if !isCollectionJob(job)}<span class="ml-3">Direct</span>{/if}
      </div>
      <div class="mt-2 flex flex-wrap items-center gap-2 text-xs text-base-content/60">
        <span class={['badge badge-sm rounded text-[11px]', statusTone(job.status)]}>{displayStatus(row)}</span>
        <span>{displayStage(row)}</span>
      </div>
      {#if job.failureMessage}<p class="mt-2 break-words text-xs text-error">{job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}</p>{/if}
      {#if isActive(job.status) && hasActiveDownloadProgress(row.progress)}
        <progress class="progress progress-primary mt-3 block h-1 w-full" value={percent} max="100" aria-label="Current transfer"></progress>
        <div class="mt-1 flex flex-wrap justify-between gap-2 text-xs tabular-nums text-base-content/55">
          <span>{row.progress?.phase || 'Transfer'}: {formatPercent(row)} · {formatOptionalBytes(downloadedBytesFor(row))} / {formatOptionalBytes(row.progress?.totalBytes ?? job.fileSizeBytes)}</span>
          <span>{formatSpeed(row.progress?.speed)} · {formatEta(row.progress?.etaSeconds)} remaining</span>
        </div>
      {/if}
      <div class="mt-3 flex flex-wrap items-center gap-2">
      {#if isDone(job.status) && mediaGuid}
        <a
          href={`/watch/${mediaGuid}`}
          onclick={stop}
          class="btn btn-sm btn-accent text-xs"
          title="Watch"
          aria-label="Watch"
        >
          <Play class="h-4 w-4" />
          Watch
        </a>
      {/if}
      {#if canUpdatePriority(job)}
        <button
          type="button"
          class="inline-flex h-9 w-9 items-center justify-center rounded-field border-[length:var(--border)] border-base-content/20 bg-base-200/70 text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary disabled:opacity-40"
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
            <Flame class="h-4 w-4" />
          {/if}
        </button>
      {/if}
      {#if canStart(job.status)}
        <button
          type="button"
          class="btn btn-sm btn-accent text-xs disabled:opacity-40"
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
            <Play class="h-4 w-4" />
          {/if}
          Retry
        </button>
      {/if}
      {#if job.failureCode === 'provider_circuit_open' || job.failureKind?.toLowerCase() === 'providerblocked'}
        <button
          type="button"
          class="inline-flex h-9 items-center justify-center gap-1.5 rounded-field border-[length:var(--border)] border-warning/35 bg-warning/10 px-2 text-[11px] font-semibold text-warning transition hover:border-warning/70 hover:bg-warning/20 disabled:opacity-40"
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
            <Repeat class="h-3.5 w-3.5" />
          {/if}
          Clear block
        </button>
      {/if}
      {#if canStop(job.status)}
        <button
          type="button"
          class="btn btn-sm btn-accent text-xs disabled:opacity-40"
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
            <Square class="h-4 w-4" />
          {/if}
          Cancel
        </button>
      {/if}
      <a
        href={job.sourceUrl}
        target="_blank"
        rel="noreferrer"
        onclick={stop}
        class="btn btn-sm btn-accent text-xs"
        title="View source"
        aria-label="View source"
      >
        <ExternalLink class="h-4 w-4" />
        View source
      </a>
      </div>
      <button class="mt-3 flex max-w-full items-center gap-1.5 text-left text-xs text-base-content/60 hover:text-base-content" onclick={toggleExpanded} aria-expanded={expanded} aria-controls={`job-details-${job.jobId}`}>
        <ListChecks class="h-3.5 w-3.5 shrink-0" />
        <span>{isQueued(job.status) ? 'Workflow not started' : isDone(job.status) ? 'Workflow finished' : `Workflow · ${displayStage(row)}`}</span>
        <ChevronDown class={['h-3.5 w-3.5 shrink-0 transition-transform motion-reduce:transition-none', expanded && 'rotate-180']} />
      </button>
    </div>
    <div class="flex flex-col items-end gap-1.5">
      <span class="badge badge-sm badge-ghost gap-1 rounded text-[11px] text-base-content/60">
        {#if job.sourceKind.toLowerCase() === 'channel'}<Tv class="h-3 w-3" />Channel
        {:else if job.sourceKind.toLowerCase() === 'playlist'}<ListVideo class="h-3 w-3" />Playlist
        {:else}<FileVideo class="h-3 w-3" />Single{/if}
      </span>
      {#if isCollectionJob(job)}
        <button type="button" class="btn btn-sm btn-accent gap-1 text-xs" onclick={() => oncollection?.(job.correlationId)}><Folder class="h-4 w-4" />Collection</button>
      {/if}
    </div>
  </div>

  {#if expanded}
    <div id={`job-details-${job.jobId}`} class="border-t border-base-300 bg-base-200/40 p-3.5 sm:pl-15">
      <div class="tabs tabs-border mb-3" role="group" aria-label="Job detail view">
        {#each ['Stages', 'Logs', 'Runs & IDs'] as tab}
          <button class={['tab h-8 px-0 mr-5 text-xs', detailTab === tab && 'tab-active']} aria-pressed={detailTab === tab} onclick={() => detailTab = tab}>{tab}</button>
        {/each}
      </div>
      {#if detailTab === 'Stages'}
        <JobStages {job} {history} />
      {:else if detailTab === 'Runs & IDs'}
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

      <div class="mt-1.5 flex flex-wrap gap-x-4 gap-y-1.5 text-xs text-base-content/50">
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Option set</span>
          <span class="text-base-content/60">{optionSetLabel(history)}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Downloaded</span>
          <span class="text-base-content/60">{formatOptionalBytes(downloadedBytesFor(row))}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Total size</span>
          <span class="text-base-content/60">{hasCurrentRunMetrics(row) ? formatOptionalBytes(row.progress?.totalBytes ?? job.fileSizeBytes) : '-'}</span>
        </span>
      </div>

      {:else}
      <div
        bind:this={logEl}
        onscroll={handleLogScroll}
        class="mt-3 max-h-48 overflow-y-auto rounded-field border-[length:var(--border)] border-base-300 bg-base-200 p-3 font-mono text-xs text-base-content"
      >
        {#if job.failureMessage}
          <p class="flex items-start gap-1.5 whitespace-pre-wrap break-words text-error">
            <CircleAlert class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            {job.failureKind ? `[${job.failureKind}] ` : ''}{job.failureCode ? `${job.failureCode}: ` : ''}{job.failureMessage}
          </p>
        {/if}
        {#if history === 'loading'}
          <p class="text-base-content">Loading history…</p>
        {:else if history === 'error'}
          <p class="text-error">Could not load job history.</p>
        {:else if history === undefined}
          <p class="text-base-content">-</p>
        {:else if history.length === 0}
          <p class="text-base-content">No recorded events.</p>
        {:else}
          {#each history as entry (entry.id)}
            <p class="whitespace-pre-wrap break-words text-base-content">
              <span class="text-base-content">[{formatLogTime(entry.recordedAt)}]</span>
              {entry.eventName === 'ProgressLine' ? entry.payloadJson : entry.eventName}
            </p>
          {/each}
        {/if}
        {#each liveMessages as entry (entry.at)}
          <p class="whitespace-pre-wrap break-words text-base-content">{entry.text}</p>
        {/each}
      </div>
      {/if}
    </div>
  {/if}
</article>
