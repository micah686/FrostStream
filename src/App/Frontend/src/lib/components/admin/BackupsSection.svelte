<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    CircleAlert,
    CircleCheck,
    CircleX,
    CloudUpload,
    Copy,
    FileArchive,
    Play,
    RefreshCw,
    Terminal,
    X
  } from '@lucide/svelte';
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

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
  const rowActionClass = 'btn btn-sm btn-neutral text-xs';

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
  let activeRestorePath = $state<string | null>(null);
  let copiedRestoreCommand = $state(false);
  let restoreOptionRequestId = 0;

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
    if (activeRestorePath === archive.archivePath) {
      closeRestorePlan();
      return;
    }

    if (restorePlans[archive.archivePath]) {
      restorePlans = {
        ...restorePlans,
        [archive.archivePath]: withRestorePlanFallback(restorePlans[archive.archivePath], archive.mode)
      };
      activeRestorePath = archive.archivePath;
      return;
    }

    planBusyPath = archive.archivePath;
    try {
      const plan = await buildRestorePlan(archive.archivePath);
      restorePlans = { ...restorePlans, [archive.archivePath]: withRestorePlanFallback(plan, archive.mode) };
      activeRestorePath = archive.archivePath;
    } catch (err) {
      restorePlans = {
        ...restorePlans,
        [archive.archivePath]: {
          preflightOk: false,
          explanation: '',
          restoreCommand: '',
          options: [],
          errorMessage: err instanceof Error ? err.message : 'Restore plan request failed.'
        }
      };
    } finally {
      planBusyPath = null;
    }
  }

  function withRestorePlanFallback(plan: RestorePlan, mode: string): RestorePlan {
    const normalized = {
      ...plan,
      explanation: plan.explanation || 'Stop FrostStream services before restoring. This is a cold/offline operation; restart services and reindex metadata afterward.',
      options: plan.options ?? []
    };
    if (normalized.options.length || !['full', 'wal-archive', 'walarchive'].includes(mode.toLowerCase())) {
      return normalized;
    }

    return {
      ...normalized,
      explanation: 'Stop FrostStream and PostgreSQL before restoring. A full restore rebuilds the PostgreSQL data directory.\n\nFor point-in-time recovery, make the WAL archive available inside the backupservice container and enter its mounted path. Most people should then enter the date and time they want to restore to, in UTC. For example, 2026-07-31 12:00:00+00 means July 31, 2026 at noon UTC. Choose exactly one recovery target. The generated command adds --archive-dir and the selected target.',
      options: [
        {
          key: 'pgdata',
          label: 'PostgreSQL data directory',
          description: 'The empty data directory that will be rebuilt.',
          inputType: 'text',
          value: null,
          placeholder: '<PGDATA>',
          required: true
        },
        {
          key: 'pg-ctl',
          label: 'pg_ctl path (optional)',
          description: 'Leave blank to use pg_ctl from PATH.',
          inputType: 'text',
          value: null,
          placeholder: 'pg_ctl',
          required: false
        },
        {
          key: 'archive-dir',
          label: 'WAL archive directory',
          description: 'Path inside the backupservice container containing the archived WAL segments.',
          inputType: 'text',
          value: null,
          placeholder: '<WAL_ARCHIVE_DIR>',
          required: false
        },
        {
          key: 'target-time',
          label: 'Restore to a date and time (optional)',
          description: 'Use this when you want the database restored to how it looked at a particular moment. Enter the time in UTC. For example, 2026-07-31 12:00:00+00 means July 31, 2026 at noon UTC. Choose only one recovery target.',
          inputType: 'text',
          value: null,
          placeholder: '2026-07-31 12:00:00+00',
          required: false
        }
      ]
    };
  }

  async function updateRestoreOption(plan: RestorePlan, key: string, value: string) {
    if (!activeRestorePath) return;
    const currentPlan = restorePlans[activeRestorePath] ?? plan;
    const nextOptions = currentPlan.options.map((option) => ({
      ...option,
      value: option.key === key ? value : option.value
    }));
    const options = Object.fromEntries(nextOptions.map((option) => [option.key, option.value])) as Record<string, string | null>;
    const requestId = ++restoreOptionRequestId;

    // Keep the command responsive while the server re-verifies the archive.
    restorePlans = {
      ...restorePlans,
      [activeRestorePath]: {
        ...currentPlan,
        options: nextOptions,
        restoreCommand: updateRestoreCommand(currentPlan.restoreCommand, nextOptions)
      }
    };

    planBusyPath = activeRestorePath;
    try {
      const refreshedPlan = await buildRestorePlan(activeRestorePath, options);
      const archiveMode = archives.find((archive) => archive.archivePath === activeRestorePath)?.mode ?? '';
      if (requestId === restoreOptionRequestId && activeRestorePath) {
        const refreshedWithFallback = withRestorePlanFallback(refreshedPlan, archiveMode);
        const optimisticPlan = restorePlans[activeRestorePath];
        const serverReturnedOptions = refreshedPlan.options?.length > 0;
        const mergedPlan = serverReturnedOptions
          ? {
              ...refreshedWithFallback,
              options: refreshedWithFallback.options.map((option) =>
                Object.prototype.hasOwnProperty.call(options, option.key)
                  ? { ...option, value: options[option.key] }
                  : option
              )
            }
          : {
              ...refreshedWithFallback,
              options: optimisticPlan?.options ?? refreshedWithFallback.options,
              restoreCommand: optimisticPlan?.restoreCommand ?? refreshedWithFallback.restoreCommand
            };
        restorePlans = { ...restorePlans, [activeRestorePath]: mergedPlan };
      }
    } catch (err) {
      if (requestId === restoreOptionRequestId && activeRestorePath) {
        restorePlans = {
          ...restorePlans,
          [activeRestorePath]: {
            ...(restorePlans[activeRestorePath] ?? currentPlan),
            errorMessage: err instanceof Error ? err.message : 'Could not update the restore command.'
          }
        };
      }
    } finally {
      planBusyPath = null;
    }
  }

  function updateRestoreCommand(command: string, options: RestorePlan['options']): string {
    let updated = command;
    const valueFor = (key: string) => options.find((option) => option.key === key)?.value?.trim() ?? '';
    const removeOption = (key: string) => {
      updated = updated.replace(new RegExp(`\\s+--${key}\\s+(?:"[^"]*"|'[^']*'|\\S+)`, 'g'), '');
    };
    const setOption = (key: string, value: string) => {
      removeOption(key);
      if (value) updated += ` --${key} ${quoteCommandValue(value)}`;
    };

    setOption('pgdata', valueFor('pgdata') || '<PGDATA>');
    setOption('pg-ctl', valueFor('pg-ctl'));

    for (const target of ['target-time', 'target-lsn', 'target-name']) removeOption(target);
    removeOption('recover-latest');
    removeOption('archive-dir');

    const target = ['target-time', 'target-lsn', 'target-name'].find((key) => valueFor(key));
    if (target) {
      setOption(target, valueFor(target));
      setOption('archive-dir', valueFor('archive-dir') || '<WAL_ARCHIVE_DIR>');
    } else if (valueFor('recover-latest') === 'true') {
      updated += ' --recover-latest';
      setOption('archive-dir', valueFor('archive-dir') || '<WAL_ARCHIVE_DIR>');
    }

    return updated;
  }

  function quoteCommandValue(value: string): string {
    return /\s/.test(value) ? `"${value.replaceAll('"', '\\"')}"` : value;
  }

  async function copyRestoreCommand(command: string) {
    await navigator.clipboard.writeText(command);
    copiedRestoreCommand = true;
    window.setTimeout(() => (copiedRestoreCommand = false), 1800);
  }

  function closeRestorePlan() {
    activeRestorePath = null;
    copiedRestoreCommand = false;
  }

  function statusBadgeClass(status: BackupJob['status']): string {
    switch (status) {
      case 'completed':
        return 'badge-success text-success-content';
      case 'failed':
        return 'badge-error text-error-content';
      case 'running':
        return 'badge-primary text-primary-content';
      case 'queued':
        return 'badge-info text-info-content';
      default:
        return 'badge-neutral';
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

<!-- Run backup -->
<section class={cardClass} aria-labelledby="backups-run-title">
  <h2 id="backups-run-title" class="text-base font-bold text-base-content">Backups</h2>
  <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
    Core-data backups cover the FrostStream, Authentik, and OpenFGA databases plus OpenBao secrets. Media files and
    rebuildable search or queue state are excluded.
  </p>

  {#if startError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{startError}</span>
    </div>
  {/if}

  <form onsubmit={runBackup} class="mt-5 flex flex-col gap-3 sm:flex-row sm:items-end">
    <div class="min-w-0 flex-1 sm:max-w-xs">
      <label class="label mb-2 text-sm" for="backup-name">Backup name (optional)</label>
      <input class="input w-full" id="backup-name" maxlength={100} bind:value={backupName} placeholder="pre-upgrade" />
    </div>
    <div class="min-w-0 flex-1 sm:max-w-sm">
      <label class="label mb-2 text-sm" for="backup-mode">Mode</label>
      <Select id="backup-mode" bind:value={backupMode} items={backupModeOptions} />
    </div>
    <button class="btn btn-sm btn-primary text-xs sm:-translate-y-1" type="submit" disabled={startBusy}>
      {#if startBusy}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Play class="mr-1.5 h-4 w-4" />
      {/if}
      Run backup now
    </button>
  </form>
  <p class="mt-2 text-xs text-base-content/50">{backupModeHints[backupMode]}</p>
</section>

<!-- Jobs -->
<section class={cardClass} aria-labelledby="backups-jobs-title">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h2 id="backups-jobs-title" class="text-base font-bold text-base-content">Backup jobs</h2>
      <p class="mt-2 text-sm text-base-content/60">
        Jobs started by the current server process. This list resets when the server restarts.
      </p>
    </div>
    <button class="btn btn-sm btn-neutral" disabled={jobsLoading} onclick={() => void loadJobs(true)}>
      <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </button>
  </div>

  {#if jobsError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{jobsError}</span>
    </div>
  {/if}

  {#if jobsLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if jobs.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <CloudUpload class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No backup jobs yet</p>
      <p class="mt-1 text-sm text-base-content/50">Run a backup to see its progress here.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each jobs as job (job.jobId)}
        <article class="rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 sm:px-4">
          <div class="flex flex-wrap items-center gap-2">
            {#if job.status === 'queued' || job.status === 'running'}
              <span class="loading loading-spinner loading-xs"></span>
            {/if}
            <span class="badge badge-sm rounded-full text-[10px] uppercase {statusBadgeClass(job.status)}">
              {job.status}
            </span>
            <span class="text-xs text-base-content/60">Started {formatDate(job.createdAt)}</span>
            {#if job.completedAt}
              <span class="text-xs text-base-content/50">· finished {formatDate(job.completedAt)}</span>
            {/if}
            <span class="ml-auto font-mono text-[10px] text-base-content/40">{job.jobId}</span>
          </div>
          {#if job.archivePath}
            <p class="mt-2 truncate font-mono text-xs text-base-content/60" title={job.archivePath}>{job.archivePath}</p>
          {/if}
          {#if job.errorMessage}
            <p class="mt-2 text-xs text-error">{job.errorMessage}</p>
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
      <h2 id="backups-archives-title" class="text-base font-bold text-base-content">Backup archives</h2>
      <p class="mt-2 text-sm text-base-content/60">
        Archives found in the server's backup directory. Verify an archive before relying on it, or build the offline
        restore command.
      </p>
    </div>
    <button class="btn btn-sm btn-neutral" disabled={archivesLoading} onclick={() => void loadArchives()}>
      <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </button>
  </div>

  {#if archivesError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{archivesError}</span>
    </div>
  {/if}

  {#if archivesLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if archives.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <FileArchive class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No backup archives found</p>
      <p class="mt-1 text-sm text-base-content/50">Completed backups appear here once written to the backup directory.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each archives as archive (archive.archivePath)}
        {@const verifyResult = verifyResults[archive.archivePath]}
        {@const plan = restorePlans[archive.archivePath]}
        <article class="rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 sm:px-4">
          <div class="flex flex-col gap-3 sm:flex-row sm:items-center">
            <div class="flex min-w-0 items-center gap-3">
              <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
                <FileArchive class="h-4.5 w-4.5" />
              </span>
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 class="truncate text-sm font-semibold text-base-content" title={archive.archivePath}>
                    {archiveName(archive.archivePath)}
                  </h3>
                  <span class="badge badge-sm badge-accent text-[10px] font-semibold text-accent-content">
                    {formatMode(archive.mode)}
                  </span>
                  <span class="badge badge-sm badge-neutral text-[10px] font-semibold">
                    schema v{archive.schemaVersion}
                  </span>
                  {#if !archive.mediaIncluded}
                    <span class="badge badge-sm badge-neutral text-[10px] font-semibold">
                      media excluded
                    </span>
                  {/if}
                </div>
                <p class="mt-0.5 truncate text-xs text-base-content/60">Created {formatDate(archive.createdAt)}</p>
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
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <CircleCheck class="h-4 w-4" />
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
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <Terminal class="h-4 w-4" />
                {/if}
                {activeRestorePath === archive.archivePath ? 'Hide restore plan' : 'Restore plan'}
              </button>
            </div>
          </div>

          {#if verifyResult}
            <div
              class={[
                'mt-3 flex items-start gap-2 rounded-lg border p-3 text-xs',
                verifyResult.success
                  ? 'border-info/30 bg-info/10 text-info-content'
                  : 'border-error/30 bg-error/10 text-error-content'
              ]}
              role="status"
            >
              {#if verifyResult.success}
                <CircleCheck class="mt-0.5 h-4 w-4 shrink-0" />
                <span>Backup verified: checksums and manifest are intact.</span>
              {:else}
                <CircleX class="mt-0.5 h-4 w-4 shrink-0" />
                <span>{verifyResult.errorMessage || 'Verification failed.'}</span>
              {/if}
            </div>
          {/if}

        </article>
      {/each}
    </div>
  {/if}
</section>

{#if activeRestorePath && restorePlans[activeRestorePath]}
  {@const activePlan = restorePlans[activeRestorePath]}
  {@const activeOptions = (activePlan.options ?? []).filter((option) => option.key !== 'tool-command')}
  <div class="fixed inset-0 z-50 flex items-start justify-center overflow-x-hidden overflow-y-auto bg-black/50 p-4 sm:p-8" role="dialog" aria-modal="true" aria-labelledby="restore-plan-title">
    <div class="relative my-4 min-w-0 w-full max-w-3xl rounded-box bg-base-100 p-6 shadow-2xl sm:my-8">
      <div class="flex items-start justify-between gap-4">
        <div>
          <h2 id="restore-plan-title" class="text-lg font-bold">Restore plan</h2>
          <p class="mt-1 font-mono text-xs text-base-content/50">{archiveName(activeRestorePath)}</p>
        </div>
        <button class="btn btn-sm btn-circle btn-ghost" type="button" aria-label="Close restore plan" onclick={closeRestorePlan}>
          <X class="h-5 w-5" />
        </button>
      </div>

      <details class="mt-5 rounded-lg border border-primary/20 bg-primary/5 p-4 text-sm text-base-content/75">
        <summary class="cursor-pointer font-semibold text-base-content">Restore guidance</summary>
        <p class="mt-3 whitespace-pre-line break-words leading-6">{activePlan.explanation}</p>
      </details>

      {#if activeOptions.length > 0}
        <div class="mt-5 space-y-4">
          <div>
            <h3 class="text-sm font-semibold">Restore options</h3>
            <p class="mt-1 text-xs text-base-content/55">Fill in the values for this environment. The command updates automatically.</p>
          </div>
          {#each activeOptions as option (option.key)}
            <div class="form-control">
              {#if option.inputType === 'checkbox'}
                <label class="label cursor-pointer justify-start gap-3">
                  <input
                    class="checkbox checkbox-sm checkbox-primary"
                    type="checkbox"
                    checked={option.value === 'true'}
                    onchange={(event) => void updateRestoreOption(activePlan, option.key, (event.currentTarget as HTMLInputElement).checked ? 'true' : 'false')}
                  />
                  <span class="min-w-0 w-full">
                    <span class="block whitespace-normal break-words text-sm font-semibold">{option.label}</span>
                    <span class="block whitespace-normal break-words text-xs leading-5 text-base-content/55">{option.description}</span>
                  </span>
                </label>
              {:else}
                <label class="label" for={'restore-' + option.key}>
                  <span class="min-w-0 w-full">
                    <span class="block whitespace-normal break-words text-sm font-semibold">{option.label}{option.required ? ' *' : ''}</span>
                    <span class="block whitespace-normal break-words text-xs leading-5 text-base-content/55">{option.description}</span>
                  </span>
                </label>
                <input
                  class="input input-bordered input-sm w-full font-mono"
                  id={'restore-' + option.key}
                  value={option.value || ''}
                  placeholder={option.placeholder || ''}
                  oninput={(event) => void updateRestoreOption(activePlan, option.key, (event.currentTarget as HTMLInputElement).value)}
                />
              {/if}
            </div>
          {/each}
        </div>
      {/if}

      <div class="mt-5">
        <div class="flex items-center justify-between gap-3">
          <h3 class="text-sm font-semibold">Command</h3>
          <button class="btn btn-sm btn-outline" type="button" disabled={!activePlan.restoreCommand} onclick={() => void copyRestoreCommand(activePlan.restoreCommand)}>
            <Copy class="mr-1.5 h-4 w-4" />
            {copiedRestoreCommand ? 'Copied' : 'Copy command'}
          </button>
        </div>
        <p class="mt-2 text-xs text-base-content/60">
          {activePlan.preflightOk ? 'Preflight checks passed. Stop all FrostStream services before running this offline command.' : 'Preflight checks failed — resolve the issue before restoring.'}
        </p>
        {#if activePlan.errorMessage}
          <p class="mt-2 text-xs text-error">{activePlan.errorMessage}</p>
        {/if}
        <pre class="mt-3 max-h-48 overflow-auto whitespace-pre-wrap break-all rounded-lg bg-black/70 p-3 font-mono text-xs leading-5 text-white/85">{activePlan.restoreCommand || 'No command available.'}</pre>
      </div>
    </div>
  </div>
{/if}
