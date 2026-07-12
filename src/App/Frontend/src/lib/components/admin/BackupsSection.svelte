<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Badge, Button, Input, Label, Select, Spinner } from 'flowbite-svelte';
  import {
    CheckCircleOutline,
    CloseCircleOutline,
    CloudArrowUpOutline,
    ExclamationCircleOutline,
    FileZipOutline,
    PlayOutline,
    RefreshOutline,
    TerminalOutline
  } from 'flowbite-svelte-icons';
  import {
    buildRestorePlan,
    listBackupJobs,
    listBackups,
    startBackup,
    verifyBackup,
    type BackupJob,
    type BackupMode,
    type BackupSummary,
    type RestorePlan,
    type VerifyBackupResult
  } from '$lib/api/backups';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';

  const backupModeOptions: { value: BackupMode; name: string }[] = [
    { value: 'snapshot', name: 'Snapshot — quick logical pg_dump (default)' },
    { value: 'full', name: 'Full — physical pg_basebackup (PITR base)' },
    { value: 'wal-archive', name: 'WAL archive — initialize continuous archiving' }
  ];

  const backupModeHints: Record<BackupMode, string> = {
    snapshot: 'Per-database logical dump plus OpenBao secrets. Best for routine restores.',
    full: 'Physical cluster base backup. Pair with WAL archiving for point-in-time recovery.',
    'wal-archive': 'Initializes the continuous WAL archive store and prints the server settings to apply.'
  };

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';
  const rowActionClass =
    'inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-50';

  const JOB_POLL_INTERVAL_MS = 4000;

  // Run backup
  let backupName = $state('');
  let backupMode = $state<BackupMode>('snapshot');
  let startBusy = $state(false);
  let startError = $state<string | null>(null);

  // Jobs
  let jobs = $state<BackupJob[]>([]);
  let jobsLoading = $state(true);
  let jobsError = $state<string | null>(null);
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  // Archives
  let archives = $state<BackupSummary[]>([]);
  let archivesLoading = $state(true);
  let archivesError = $state<string | null>(null);

  // Per-archive verify / restore plan
  let verifyBusyPath = $state<string | null>(null);
  let verifyResults = $state<Record<string, VerifyBackupResult>>({});
  let planBusyPath = $state<string | null>(null);
  let restorePlans = $state<Record<string, RestorePlan>>({});

  const hasActiveJobs = $derived(jobs.some((job) => job.status === 'queued' || job.status === 'running'));

  onMount(() => {
    void loadJobs(true);
    void loadArchives();
  });

  onDestroy(() => stopPolling());

  function startPolling() {
    if (pollTimer !== null) {
      return;
    }
    pollTimer = setInterval(() => void loadJobs(false), JOB_POLL_INTERVAL_MS);
  }

  function stopPolling() {
    if (pollTimer !== null) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  async function loadJobs(showSpinner: boolean) {
    if (showSpinner) {
      jobsLoading = true;
    }
    try {
      const previousActive = hasActiveJobs;
      jobs = await listBackupJobs();
      jobsError = null;
      if (hasActiveJobs) {
        startPolling();
      } else {
        stopPolling();
        if (previousActive) {
          // A job just finished; the archive list may have a new entry.
          void loadArchives();
        }
      }
    } catch (err) {
      jobsError = err instanceof Error ? err.message : 'Could not load backup jobs.';
      stopPolling();
    } finally {
      jobsLoading = false;
    }
  }

  async function loadArchives() {
    archivesLoading = true;
    archivesError = null;
    try {
      archives = await listBackups();
    } catch (err) {
      archivesError = err instanceof Error ? err.message : 'Could not load backup archives.';
    } finally {
      archivesLoading = false;
    }
  }

  async function runBackup(event: SubmitEvent) {
    event.preventDefault();
    startBusy = true;
    startError = null;
    try {
      const job = await startBackup(backupName, backupMode);
      backupName = '';
      jobs = [job, ...jobs.filter((item) => item.jobId !== job.jobId)];
      startPolling();
    } catch (err) {
      startError = err instanceof Error ? err.message : 'Could not start the backup.';
    } finally {
      startBusy = false;
    }
  }

  async function verify(archive: BackupSummary) {
    verifyBusyPath = archive.archivePath;
    try {
      verifyResults = { ...verifyResults, [archive.archivePath]: await verifyBackup(archive.archivePath) };
    } catch (err) {
      verifyResults = {
        ...verifyResults,
        [archive.archivePath]: {
          success: false,
          errorMessage: err instanceof Error ? err.message : 'Verification request failed.'
        }
      };
    } finally {
      verifyBusyPath = null;
    }
  }

  async function showRestorePlan(archive: BackupSummary) {
    if (restorePlans[archive.archivePath]) {
      const { [archive.archivePath]: _, ...rest } = restorePlans;
      restorePlans = rest;
      return;
    }

    planBusyPath = archive.archivePath;
    try {
      restorePlans = { ...restorePlans, [archive.archivePath]: await buildRestorePlan(archive.archivePath) };
    } catch (err) {
      restorePlans = {
        ...restorePlans,
        [archive.archivePath]: {
          preflightOk: false,
          restoreCommand: '',
          errorMessage: err instanceof Error ? err.message : 'Restore plan request failed.'
        }
      };
    } finally {
      planBusyPath = null;
    }
  }

  function statusBadgeClass(status: BackupJob['status']): string {
    switch (status) {
      case 'completed':
        return 'bg-emerald-500/15! text-emerald-300!';
      case 'failed':
        return 'bg-red-500/15! text-red-300!';
      case 'running':
        return 'bg-blue-500/15! text-blue-300!';
      default:
        return 'bg-slate-800! text-slate-400!';
    }
  }

  function formatDate(value: string | null): string {
    if (!value) {
      return 'unknown';
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'unknown' : date.toLocaleString();
  }

  function archiveName(path: string): string {
    const segments = path.split(/[\\/]/);
    return segments[segments.length - 1] || path;
  }

  function formatMode(mode: string): string {
    switch (mode?.toLowerCase()) {
      case 'full':
        return 'full';
      case 'walarchive':
      case 'wal-archive':
        return 'wal-archive';
      default:
        return 'snapshot';
    }
  }
</script>

<UnderDevelopmentBanner />

<!-- Run backup -->
<section class={cardClass} aria-labelledby="backups-run-title">
  <h2 id="backups-run-title" class="text-base font-bold text-slate-100">Backups</h2>
  <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
    Core-data backups cover the FrostStream, Authentik, and OpenFGA databases plus OpenBao secrets. Media files and
    rebuildable search or queue state are excluded.
  </p>

  {#if startError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{startError}</span>
    </div>
  {/if}

  <form onsubmit={runBackup} class="mt-5 flex flex-col gap-3 sm:flex-row sm:items-end">
    <div class="min-w-0 flex-1 sm:max-w-xs">
      <Label for="backup-name" class="mb-2 text-sm font-medium text-slate-300">Backup name (optional)</Label>
      <Input id="backup-name" maxlength={100} bind:value={backupName} placeholder="pre-upgrade" class={inputClass} />
    </div>
    <div class="min-w-0 flex-1 sm:max-w-xs">
      <Label for="backup-mode" class="mb-2 text-sm font-medium text-slate-300">Mode</Label>
      <Select id="backup-mode" bind:value={backupMode} items={backupModeOptions} class={inputClass} />
    </div>
    <Button
      type="submit"
      color="blue"
      class="border-0! px-4! py-2.5! text-xs! font-semibold! disabled:opacity-60"
      disabled={startBusy}
    >
      {#if startBusy}
        <Spinner size="4" class="mr-1.5" />
      {:else}
        <PlayOutline class="mr-1.5 h-4 w-4" />
      {/if}
      Run backup now
    </Button>
  </form>
  <p class="mt-2 text-xs text-slate-500">{backupModeHints[backupMode]}</p>
</section>

<!-- Jobs -->
<section class={cardClass} aria-labelledby="backups-jobs-title">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h2 id="backups-jobs-title" class="text-base font-bold text-slate-100">Backup jobs</h2>
      <p class="mt-2 text-sm text-slate-400">
        Jobs started by the current server process. This list resets when the server restarts.
      </p>
    </div>
    <Button color="dark" class={outlineButtonClass} disabled={jobsLoading} onclick={() => void loadJobs(true)}>
      <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </Button>
  </div>

  {#if jobsError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{jobsError}</span>
    </div>
  {/if}

  {#if jobsLoading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if jobs.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <CloudArrowUpOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No backup jobs yet</p>
      <p class="mt-1 text-sm text-slate-500">Run a backup to see its progress here.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each jobs as job (job.jobId)}
        <article class="rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 sm:px-4">
          <div class="flex flex-wrap items-center gap-2">
            {#if job.status === 'queued' || job.status === 'running'}
              <Spinner size="4" />
            {/if}
            <Badge rounded class="px-2.5! py-0.5! text-[10px]! font-semibold! uppercase! {statusBadgeClass(job.status)}">
              {job.status}
            </Badge>
            <span class="text-xs text-slate-400">Started {formatDate(job.createdAt)}</span>
            {#if job.completedAt}
              <span class="text-xs text-slate-500">· finished {formatDate(job.completedAt)}</span>
            {/if}
            <span class="ml-auto font-mono text-[10px] text-slate-600">{job.jobId}</span>
          </div>
          {#if job.archivePath}
            <p class="mt-2 truncate font-mono text-xs text-slate-400" title={job.archivePath}>{job.archivePath}</p>
          {/if}
          {#if job.errorMessage}
            <p class="mt-2 text-xs text-red-300">{job.errorMessage}</p>
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>

<!-- Archives -->
<section class={cardClass} aria-labelledby="backups-archives-title">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h2 id="backups-archives-title" class="text-base font-bold text-slate-100">Backup archives</h2>
      <p class="mt-2 text-sm text-slate-400">
        Archives found in the server's backup directory. Verify an archive before relying on it, or build the offline
        restore command.
      </p>
    </div>
    <Button color="dark" class={outlineButtonClass} disabled={archivesLoading} onclick={() => void loadArchives()}>
      <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </Button>
  </div>

  {#if archivesError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{archivesError}</span>
    </div>
  {/if}

  {#if archivesLoading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if archives.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <FileZipOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No backup archives found</p>
      <p class="mt-1 text-sm text-slate-500">Completed backups appear here once written to the backup directory.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each archives as archive (archive.archivePath)}
        {@const verifyResult = verifyResults[archive.archivePath]}
        {@const plan = restorePlans[archive.archivePath]}
        <article class="rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 sm:px-4">
          <div class="flex flex-col gap-3 sm:flex-row sm:items-center">
            <div class="flex min-w-0 items-center gap-3">
              <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                <FileZipOutline class="h-4.5 w-4.5" />
              </span>
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 class="truncate text-sm font-semibold text-slate-100" title={archive.archivePath}>
                    {archiveName(archive.archivePath)}
                  </h3>
                  <span class="rounded-full bg-blue-500/15 px-2 py-0.5 text-[10px] font-semibold text-blue-300">
                    {formatMode(archive.mode)}
                  </span>
                  <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                    schema v{archive.schemaVersion}
                  </span>
                  {#if !archive.mediaIncluded}
                    <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                      media excluded
                    </span>
                  {/if}
                </div>
                <p class="mt-0.5 truncate text-xs text-slate-400">Created {formatDate(archive.createdAt)}</p>
              </div>
            </div>

            <div class="flex shrink-0 flex-wrap gap-2 sm:ml-auto">
              <button
                type="button"
                class={rowActionClass}
                disabled={verifyBusyPath === archive.archivePath}
                onclick={() => void verify(archive)}
              >
                {#if verifyBusyPath === archive.archivePath}
                  <Spinner size="4" />
                {:else}
                  <CheckCircleOutline class="h-4 w-4" />
                {/if}
                Verify
              </button>
              <button
                type="button"
                class={rowActionClass}
                disabled={planBusyPath === archive.archivePath}
                onclick={() => void showRestorePlan(archive)}
              >
                {#if planBusyPath === archive.archivePath}
                  <Spinner size="4" />
                {:else}
                  <TerminalOutline class="h-4 w-4" />
                {/if}
                {plan ? 'Hide restore plan' : 'Restore plan'}
              </button>
            </div>
          </div>

          {#if verifyResult}
            <div
              class={[
                'mt-3 flex items-start gap-2 rounded-lg border p-3 text-xs',
                verifyResult.success
                  ? 'border-emerald-900/60 bg-emerald-950/35 text-emerald-300'
                  : 'border-red-900/60 bg-red-950/35 text-red-300'
              ]}
              role="status"
            >
              {#if verifyResult.success}
                <CheckCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
                <span>Backup verified: checksums and manifest are intact.</span>
              {:else}
                <CloseCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
                <span>{verifyResult.errorMessage || 'Verification failed.'}</span>
              {/if}
            </div>
          {/if}

          {#if plan}
            <div class="mt-3 rounded-lg border border-slate-800 bg-slate-950/60 p-3">
              {#if plan.errorMessage}
                <p class="text-xs text-red-300">{plan.errorMessage}</p>
              {:else}
                <p class="text-xs text-slate-400">
                  {plan.preflightOk
                    ? 'Preflight checks passed. Stop all FrostStream services, then run:'
                    : 'Preflight checks failed — resolve the issue before restoring. Planned command:'}
                </p>
              {/if}
              {#if plan.restoreCommand}
                <pre class="mt-2 overflow-x-auto rounded bg-black/40 p-2.5 font-mono text-xs text-slate-300">{plan.restoreCommand}</pre>
              {/if}
            </div>
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>
