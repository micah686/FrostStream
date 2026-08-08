<script lang="ts">
  import { ChevronDown, CircleAlert } from '@lucide/svelte';
  import { humanizeTaskType, type BackgroundRun } from '$lib/api/backgroundJobs';

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
      return 'badge-success text-success-content';
    }
    if (status === 'failed') {
      return 'badge-error text-error-content';
    }
    return 'badge-primary text-primary-content';
  }

  function displayStatus(status: string): string {
    switch (status) {
      case 'completed':
        return 'Completed';
      case 'failed':
        return 'Failed';
      case 'running':
        return 'Downloading';
      default:
        return 'Queued';
    }
  }

  function rowTone(status: string): string {
    if (status === 'failed') {
      return 'border-error/45 bg-error/10';
    }
    if (status === 'running') {
      return 'border-primary/60 bg-primary/10';
    }
    if (status === 'queued') {
      return 'border-warning/45 bg-warning/8';
    }
    return 'border-base-300/90 bg-base-200/45';
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

<article class={['card border p-4 transition', rowTone(run.status)]}>
  <div
    class="grid cursor-pointer gap-3 md:grid-cols-[minmax(0,1fr)_5rem] md:items-center"
    role="button"
    tabindex="0"
    aria-expanded={expanded}
    onclick={() => (expanded = !expanded)}
    onkeydown={(event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        expanded = !expanded;
      }
    }}
  >
    <div class="flex min-w-0 items-start gap-3">
      <div class="min-w-0">
        <div class="flex min-w-0 items-center gap-2">
          <ChevronDown
            class={['h-3.5 w-3.5 shrink-0 text-base-content/40 transition-transform', expanded ? 'rotate-180' : '']}
          />
          <h2 class="min-w-0 truncate text-sm font-semibold text-base-content">
            {humanizeTaskType(run.taskType)}
          </h2>
          <span class={['badge badge-sm shrink-0 text-[10px] font-bold', statusTone(run.status)]}>
            {displayStatus(run.status)}
          </span>
          {#if scanLabel(run.taskType)}
            <span class="badge badge-sm shrink-0 badge-secondary text-[10px] font-bold text-secondary-content">
              {scanLabel(run.taskType)}
            </span>
          {/if}
          {#if run.trigger === 'Manual'}
            <span class="badge badge-sm shrink-0 badge-accent text-[10px] font-bold text-accent-content">
              MANUAL
            </span>
          {/if}
          {#if run.detail && !scanLabel(run.taskType)}
            <span class="badge badge-sm shrink-0 badge-ghost text-[10px] font-semibold">
              {run.detail}
            </span>
          {/if}
        </div>
        <p class="mt-1 truncate text-xs text-base-content/50">
          {run.scheduleKey ?? 'started by hand'} · {isQueued ? 'not picked up yet' : run.origin} ·
          {isQueued ? `waiting ${elapsed(run)}` : elapsed(run)}
        </p>
        {#if run.message}
          <p class="mt-1 truncate text-[11px] text-base-content/50">{run.message}</p>
        {/if}
        {#if run.errorMessage}
          <p class="mt-2 line-clamp-1 text-xs text-error">{run.errorMessage}</p>
        {/if}
      </div>
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
  </div>

  {#if expanded}
    <div class="mt-3 border-t border-base-300/70 pt-3">
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

      <div class="mt-3 max-h-48 overflow-y-auto rounded-lg border border-base-300 bg-base-200 p-3 font-mono text-xs text-base-content">
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
