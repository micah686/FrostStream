<script lang="ts">
  import { ChevronDown, CircleAlert, Clock } from '@lucide/svelte';
  import { humanizeTaskType, type BackgroundRun } from '#lib/api/backgroundJobs.js';

  let { run, now }: { run: BackgroundRun; now: number } = $props();

  let expanded = $state(false);

  const percent = $derived(percentFor(run));
  const isQueued = $derived(run.status === 'queued');

  function percentFor(r: BackgroundRun): number {
    if (r.status === 'completed') {
      return 100;
    }
    if (r.percent !== null && r.percent !== undefined) {
      return Math.min(100, Math.max(0, r.percent));
    }
    if (r.total && r.total > 0 && r.current !== null && r.current !== undefined) {
      return Math.min(100, Math.max(0, (r.current / r.total) * 100));
    }
    return 0;
  }

  function statusTone(status: string): string {
    if (status === 'completed') {
      return 'badge-soft badge-success';
    }
    if (status === 'failed') {
      return 'badge-soft badge-error';
    }
    return status === 'running' ? 'badge-soft badge-primary' : 'badge-ghost';
  }

  function displayStatus(status: string): string {
    switch (status) {
      case 'completed':
        return 'Completed';
      case 'failed':
        return 'Failed';
      case 'running':
        return 'Running';
      default:
        return 'Queued';
    }
  }

  /** Time on the clock: how long a queued run has been waiting, otherwise how long it has run. */
  function elapsed(r: BackgroundRun): string {
    const started = Date.parse(r.status === 'queued' ? (r.queuedAt ?? r.startedAt) : r.startedAt);
    const ended = r.completedAt ? Date.parse(r.completedAt) : now;
    if (Number.isNaN(started) || Number.isNaN(ended) || ended < started) {
      return '-';
    }
    return formatDurationMs(ended - started);
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

  function formatLogTime(at: string): string {
    const parsed = new Date(at);
    return Number.isNaN(parsed.getTime()) ? at : parsed.toLocaleTimeString([], { hour12: false });
  }

  function scanLabel(taskType: string): string | null {
    if (taskType === 'channel_scan_refresh') return 'INCREMENTAL SCAN';
    if (taskType === 'channel_scan_full') return 'FORCED SCAN';
    return null;
  }
</script>

<article class="card overflow-hidden rounded-lg border border-base-300 bg-base-100">
  <button type="button" class="grid w-full grid-cols-[2.25rem_minmax(0,1fr)_auto] gap-3 p-3.5 text-left max-[470px]:grid-cols-[minmax(0,1fr)_auto]" aria-expanded={expanded} aria-controls={`background-details-${run.runId}`} onclick={() => (expanded = !expanded)}>
    <span class="flex h-8 items-center justify-center rounded bg-base-200 text-base-content/50 max-[470px]:hidden"><Clock class="h-4 w-4" /></span>
    <span class="min-w-0">
      <span class="block break-words text-sm font-semibold text-base-content">{humanizeTaskType(run.taskType)}</span>
      <span class="mt-1 block text-xs text-base-content/55">{run.scheduleKey ?? 'Started by hand'} · {isQueued ? 'Awaiting pickup' : run.origin} · {isQueued ? `waiting ${elapsed(run)}` : elapsed(run)}</span>
      <span class="mt-2 flex flex-wrap items-center gap-2 text-xs text-base-content/60">
        <span class={['badge badge-sm rounded text-[11px]', statusTone(run.status)]}>{displayStatus(run.status)}</span>
        {#if scanLabel(run.taskType)}<span>{scanLabel(run.taskType)}</span>{/if}
        {#if run.detail && !scanLabel(run.taskType)}<span>{run.detail}</span>{/if}
      </span>
      {#if run.message}<span class="mt-2 block break-words text-xs text-base-content/60">{run.message}</span>{/if}
      {#if run.errorMessage}<span class="mt-2 block break-words text-xs text-error">{run.errorMessage}</span>{/if}
      {#if run.status === 'running'}
        <progress class="progress progress-primary mt-3 block h-1 w-full" value={percent} max="100" aria-label="Background run progress"></progress>
        <span class="mt-1 block text-xs tabular-nums text-base-content/55">{Math.round(percent)}% complete</span>
      {/if}
      <span class="mt-3 flex items-center gap-1.5 text-xs text-base-content/60">
        {expanded ? 'Hide details' : 'View details'}
        <ChevronDown class={['h-3.5 w-3.5 transition-transform', expanded && 'rotate-180']} />
      </span>
    </span>
    <span class="badge badge-sm badge-ghost rounded text-[11px] text-base-content/60">{run.trigger}</span>
  </button>

  {#if expanded}
    <div id={`background-details-${run.runId}`} class="border-t border-base-300 bg-base-200/40 p-3.5 sm:pl-15">
      <div class="flex flex-wrap gap-x-4 gap-y-1.5 text-xs text-base-content/50">
        <span class="inline-flex min-w-0 items-center gap-1">
          <span class="shrink-0 text-base-content/40">Run ID</span>
          <span class="break-all font-mono text-base-content/60">{run.runId}</span>
        </span>
        <span class="inline-flex min-w-0 items-center gap-1">
          <span class="shrink-0 text-base-content/40">Schedule</span>
          <span class="break-all text-base-content/60">{run.scheduleKey ?? 'none (started by hand)'}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Service</span>
          <span class="text-base-content/60">{isQueued ? 'awaiting pickup' : run.origin}</span>
        </span>
        {#if run.queuedAt}
          <span class="inline-flex items-center gap-1">
            <span class="shrink-0 text-base-content/40">Queued</span>
            <span class="text-base-content/60">{formatLogTime(run.queuedAt)}</span>
          </span>
        {/if}
        {#if !isQueued}
          <span class="inline-flex items-center gap-1">
            <span class="shrink-0 text-base-content/40">Started</span>
            <span class="text-base-content/60">{formatLogTime(run.startedAt)}</span>
          </span>
        {/if}
        {#if run.completedAt}
          <span class="inline-flex items-center gap-1">
            <span class="shrink-0 text-base-content/40">Finished</span>
            <span class="text-base-content/60">{formatLogTime(run.completedAt)}</span>
          </span>
        {/if}
      </div>

      <div class="mt-3 max-h-48 overflow-y-auto rounded-field border-[length:var(--border)] border-base-300 bg-base-200 p-3 font-mono text-xs text-base-content">
        {#if run.errorMessage}
          <p class="flex items-start gap-1.5 whitespace-pre-wrap break-words text-error">
            <CircleAlert class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            {run.errorMessage}
          </p>
        {/if}
        {#if run.log.length === 0}
          <p class="text-base-content">No progress reported yet.</p>
        {:else}
          {#each run.log as line, index (`${line.at}-${index}`)}
            <p class="whitespace-pre-wrap break-words text-base-content">
              <span class="text-base-content">[{formatLogTime(line.at)}]</span>
              {line.message}
            </p>
          {/each}
        {/if}
      </div>
    </div>
  {/if}
</article>
