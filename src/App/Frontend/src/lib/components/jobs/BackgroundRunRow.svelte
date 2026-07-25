<script lang="ts">
  import { ChevronDown, CircleAlert } from '@lucide/svelte';
  import { humanizeTaskType, type BackgroundRun } from '$lib/api/backgroundJobs';

  let { run, now }: { run: BackgroundRun; now: number } = $props();

  let expanded = $state(false);

  const percent = $derived(percentFor(run));
  const isRunning = $derived(run.status === 'running');
  const isFailed = $derived(run.status === 'failed');

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
      return 'bg-success/12 text-success ring-success/20';
    }
    if (status === 'failed') {
      return 'bg-error/12 text-error ring-error/25';
    }
    return 'bg-primary/12 text-primary ring-primary/20';
  }

  function barColor(status: string): string {
    if (status === 'completed') {
      return 'bg-success';
    }
    if (status === 'failed') {
      return 'bg-error';
    }
    return 'bg-primary';
  }

  function rowTone(status: string): string {
    if (status === 'failed') {
      return 'border-error/45 bg-error/10';
    }
    if (status === 'running') {
      return 'border-primary/60 bg-primary/10';
    }
    return 'border-base-300/90 bg-base-200/45';
  }

  function elapsed(r: BackgroundRun): string {
    const started = Date.parse(r.startedAt);
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

  function countLabel(r: BackgroundRun): string | null {
    if (r.current === null || r.current === undefined || !r.total) {
      return null;
    }
    return `${r.current} / ${r.total}`;
  }

  function taskInitial(taskType: string): string {
    return taskType.replace(/[_-]+/g, ' ').trim().charAt(0).toUpperCase() || '?';
  }
</script>

<article class={['rounded-xl border p-4 shadow-lg shadow-black/10 transition', rowTone(run.status)]}>
  <div
    class="grid cursor-pointer gap-3 md:grid-cols-[minmax(0,1fr)_18rem] md:items-center"
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
      <span
        class="mt-0.5 grid h-10 w-10 shrink-0 place-items-center rounded-lg bg-base-300 text-sm font-bold text-primary ring-1 ring-base-content/20"
        aria-hidden="true"
      >
        {taskInitial(run.taskType)}
      </span>
      <div class="min-w-0">
        <div class="flex min-w-0 items-center gap-2">
          <ChevronDown
            class={['h-3.5 w-3.5 shrink-0 text-base-content/40 transition-transform', expanded ? 'rotate-180' : '']}
          />
          <h2 class="min-w-0 truncate text-sm font-semibold text-base-content">
            {humanizeTaskType(run.taskType)}
          </h2>
          <span class={['shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', statusTone(run.status)]}>
            {run.status}
          </span>
          {#if run.trigger === 'Manual'}
            <span class="shrink-0 rounded-full bg-accent/12 px-2 py-0.5 text-[10px] font-bold text-accent ring-1 ring-accent/25">
              MANUAL
            </span>
          {/if}
          {#if run.detail}
            <span class="shrink-0 rounded-full bg-base-300/60 px-2 py-0.5 text-[10px] font-semibold text-base-content/70 ring-1 ring-base-content/15">
              {run.detail}
            </span>
          {/if}
        </div>
        <p class="mt-1 truncate text-xs text-base-content/50">
          {run.scheduleKey ?? 'started by hand'} · {run.origin} · {elapsed(run)}
        </p>
        {#if run.message}
          <p class="mt-1 truncate text-[11px] text-base-content/50">{run.message}</p>
        {/if}
        {#if run.errorMessage}
          <p class="mt-2 line-clamp-1 text-xs text-error">{run.errorMessage}</p>
        {/if}
      </div>
    </div>

    <div class="flex items-center gap-3">
      <div class="min-w-0 flex-1">
        <div class="h-1.5 w-full overflow-hidden rounded-full bg-base-300">
          <div
            class={[
              'h-full rounded-full transition-[width]',
              barColor(run.status),
              isRunning && percent === 0 ? 'animate-pulse' : ''
            ]}
            style={`width: ${isRunning && percent === 0 ? 100 : percent}%`}
          ></div>
        </div>
        <p class="mt-1 text-xs text-base-content/50">
          {#if isRunning && percent === 0}
            working…
          {:else}
            {Math.round(percent)}%
          {/if}
          {#if countLabel(run)}
            · {countLabel(run)}
          {/if}
        </p>
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
          <span class="text-base-content/60">{run.origin}</span>
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="shrink-0 text-base-content/40">Started</span>
          <span class="text-base-content/60">{formatLogTime(run.startedAt)}</span>
        </span>
        {#if run.completedAt}
          <span class="inline-flex items-center gap-1">
            <span class="shrink-0 text-base-content/40">Finished</span>
            <span class="text-base-content/60">{formatLogTime(run.completedAt)}</span>
          </span>
        {/if}
      </div>

      <div class="mt-3 max-h-48 overflow-y-auto rounded-lg border border-base-300/80 bg-base-200/60 p-3 font-mono text-xs">
        {#if run.errorMessage}
          <p class="flex items-start gap-1.5 whitespace-pre-wrap break-words text-error">
            <CircleAlert class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            {run.errorMessage}
          </p>
        {/if}
        {#if run.log.length === 0}
          <p class="text-base-content/40">No progress reported yet.</p>
        {:else}
          {#each run.log as line, index (`${line.at}-${index}`)}
            <p class="whitespace-pre-wrap break-words text-base-content/60">
              <span class="text-base-content/40">[{formatLogTime(line.at)}]</span>
              {line.message}
            </p>
          {/each}
        {/if}
      </div>
    </div>
  {/if}
</article>
