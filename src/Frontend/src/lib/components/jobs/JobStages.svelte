<script lang="ts">
  import { Check, CircleAlert, LoaderCircle, Minus } from '@lucide/svelte';
  import type { DownloadQueueJob, DownloadQueueHistoryEntry } from '#lib/api/downloadQueue.js';
  import { humanizeDownloadName } from '#lib/jobs/jobState.js';

  let { job, history }: {
    job: DownloadQueueJob;
    history: DownloadQueueHistoryEntry[] | 'loading' | 'error' | undefined;
  } = $props();

  const phases = [
    { name: 'Prepare', stages: ['Metadata', 'DuplicateCheck', 'WaitingForWorker'] },
    { name: 'Download', stages: ['MediaAcquire'] },
    { name: 'Store', stages: ['PrimaryMediaUpload', 'MetaSidecarUpload', 'InfoJsonUpload', 'ThumbnailUpload', 'CaptionUpload', 'LiveChatUpload', 'RichMetadataWrite'] },
    { name: 'Finish', stages: ['Finalize', 'Cleanup', 'Compensation'] }
  ];

  // Only report observed outcomes; optional stages and previous runs must not look completed.
  const states = $derived.by(() => {
    const result = new Map<string, string>();
    if (Array.isArray(history)) {
      for (const entry of history) {
        try {
          const payload = JSON.parse(entry.payloadJson ?? '{}');
          const execution = payload.execution ?? payload.Execution ?? payload;
          const runId = execution.runId ?? execution.RunId;
          const recordedStage = execution.stage ?? execution.Stage;
          const stage = typeof recordedStage === 'number'
            ? ['None', 'Metadata', 'DuplicateCheck', 'WaitingForWorker', 'MediaAcquire', 'PrimaryMediaUpload', 'MetaSidecarUpload', 'InfoJsonUpload', 'ThumbnailUpload', 'CaptionUpload', 'RichMetadataWrite', 'Finalize', 'Cleanup', 'Compensation', 'LiveChatUpload'][recordedStage]
            : recordedStage;
          if (!stage || !runId || runId !== job.runId) continue;
          const event = entry.eventName.replace(/^DownloadStage/, '');
          const state = ({ MetadataFetched: 'Succeeded', MetadataFetchFailed: 'Failed', DownloadCompleted: 'Succeeded', DownloadFailed: 'Failed', UploadCompleted: 'Succeeded', UploadFailed: 'Failed', TempFileDeleted: 'Succeeded', TempFileDeleteFailed: 'Failed', UploadedObjectDeleted: 'Succeeded', UploadedObjectDeleteFailed: 'Failed', Started: 'Running', Succeeded: 'Succeeded', Failed: 'Failed', Skipped: 'Skipped', Warning: 'Warning', Stopped: 'Stopped' } as Record<string, string>)[event];
          if (state) result.set(stage, state);
        } catch { /* Non-stage timeline entries may contain plain text. */ }
      }
    }
    if (job.stage !== 'None') result.set(job.stage, job.stageStatus);
    return result;
  });
</script>

{#if history === 'loading'}<p class="mb-2 text-xs text-base-content/50">Loading recorded stages…</p>
{:else if history === 'error'}<p class="mb-2 text-xs text-error">Could not load recorded stages. Showing the current stage.</p>{/if}
{#each phases as phase}
  <details class="collapse collapse-arrow rounded-none border-b border-base-300 text-xs" open={phase.stages.includes(job.stage)}>
    <summary class="collapse-title min-h-0 py-3 pl-0 text-xs font-medium">
      {phase.name}
      <span class="ml-2 font-normal text-base-content/55">{phase.stages.includes(job.stage) ? humanizeDownloadName(job.stageStatus) : `${phase.stages.filter(stage => states.get(stage) === 'Succeeded').length} recorded complete`}</span>
    </summary>
    <ul class="collapse-content space-y-2 pl-3 text-xs">
      {#each phase.stages as stage}
        {@const status = states.get(stage)}
        <li class={['flex items-center gap-2', status === 'Running' ? 'text-primary' : status === 'Failed' ? 'text-error' : 'text-base-content/60']}>
          {#if status === 'Succeeded'}<Check class="h-3.5 w-3.5" />
          {:else if status === 'Failed'}<CircleAlert class="h-3.5 w-3.5" />
          {:else if status === 'Running'}<LoaderCircle class="h-3.5 w-3.5 motion-safe:animate-spin" />
          {:else}<Minus class="h-3.5 w-3.5" />{/if}
          <span class="flex-1">{humanizeDownloadName(stage)}</span>
          <span class="text-[11px]">{status ? humanizeDownloadName(status) : 'Not recorded'}</span>
        </li>
      {/each}
    </ul>
  </details>
{/each}
