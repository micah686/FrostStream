<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    CircleAlert,
    CircleCheck,
    CloudUpload,
    FileArchive,
    LifeBuoy,
    Play,
    RefreshCw,
    ShieldCheck
  } from '@lucide/svelte';
  import {
    listBackupJobs,
    listBackups,
    startBackup,
    verifyBackup,
    type BackupInfo,
    type BackupJob,
    type BackupRepository,
    type BackupType
  } from '$lib/api/backups';

  const backupTypeOptions: { value: BackupType; name: string }[] = [
    { value: 'full', name: 'Full — complete cluster backup' },
    { value: 'diff', name: 'Differential — changes since the last full' }
  ];

  const backupTypeHints: Record<BackupType, string> = {
    full: 'Complete pgBackRest cluster backup plus an OpenBao secrets export. The weekly schedule takes one every Sunday.',
    diff: 'Only what changed since the last full backup — fast and small. Requires at least one full backup. The daily schedule takes one every night.'
  };

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
  const rowActionClass = 'btn btn-sm btn-neutral text-xs';

  const JOB_POLL_INTERVAL_MS = 4000;

  // Run backup
  let backupName = $state('');
  let backupType = $state<BackupType>('full');
  let startBusy = $state(false);
  let startError = $state<string | null>(null);

  // Jobs
  let jobs = $state<BackupJob[]>([]);
  let jobsLoading = $state(true);
  let jobsError = $state<string | null>(null);
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  // Repository
  let repository = $state<BackupRepository | null>(null);
  let repositoryLoading = $state(true);
  let repositoryError = $state<string | null>(null);

  // Verify actions
  let quickVerifyBusy = $state(false);
  let deepVerifyBusyLabel = $state<string | null>(null);
  let verifyError = $state<string | null>(null);

  let restoreUiUrl = $state('');

  const hasActiveJobs = $derived(jobs.some((job) => job.status === 'queued' || job.status === 'running'));

  onMount(() => {
    restoreUiUrl = `http://${window.location.hostname}:25900/`;
    void loadJobs(true);
    void loadRepository();
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
          // A job just finished; the repository may have a new backup or verify result.
          void loadRepository();
        }
      }
    } catch (err) {
      jobsError = err instanceof Error ? err.message : 'Could not load backup jobs.';
      stopPolling();
    } finally {
      jobsLoading = false;
    }
  }

  async function loadRepository() {
    repositoryLoading = true;
    repositoryError = null;
    try {
      repository = await listBackups();
    } catch (err) {
      repositoryError = err instanceof Error ? err.message : 'Could not load the backup repository.';
    } finally {
      repositoryLoading = false;
    }
  }

  async function runBackup(event: SubmitEvent) {
    event.preventDefault();
    startBusy = true;
    startError = null;
    try {
      const job = await startBackup(backupName, backupType);
      backupName = '';
      mergeJob(job);
    } catch (err) {
      startError = err instanceof Error ? err.message : 'Could not start the backup.';
    } finally {
      startBusy = false;
    }
  }

  async function runQuickVerify() {
    quickVerifyBusy = true;
    verifyError = null;
    try {
      mergeJob(await verifyBackup(null, false));
    } catch (err) {
      verifyError = err instanceof Error ? err.message : 'Could not start the verification.';
    } finally {
      quickVerifyBusy = false;
    }
  }

  async function runDeepVerify(backup: BackupInfo) {
    deepVerifyBusyLabel = backup.label;
    verifyError = null;
    try {
      mergeJob(await verifyBackup(backup.label, true));
    } catch (err) {
      verifyError = err instanceof Error ? err.message : 'Could not start the deep verification.';
    } finally {
      deepVerifyBusyLabel = null;
    }
  }

  function mergeJob(job: BackupJob) {
    jobs = [job, ...jobs.filter((item) => item.jobId !== job.jobId)];
    startPolling();
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

  function describeKind(job: BackupJob): string {
    switch (job.kind) {
      case 'backup':
        return job.type === 'diff' ? 'diff backup' : 'full backup';
      case 'verify-quick':
        return 'quick verify';
      case 'verify-deep':
        return 'deep verify';
      case 'restore':
        return 'restore';
      default:
        return job.kind;
    }
  }

  function formatDate(value: string | null): string {
    if (!value) {
      return 'unknown';
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'unknown' : date.toLocaleString();
  }

  function formatSize(bytes: number | null): string {
    if (bytes === null || bytes === undefined) {
      return '—';
    }
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GiB`;
    if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MiB`;
    if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
    return `${bytes} B`;
  }
</script>

<!-- Run backup -->
<section class={cardClass} aria-labelledby="backups-run-title">
  <h2 id="backups-run-title" class="text-base font-bold text-base-content">Backups</h2>
  <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
    pgBackRest backups cover the FrostStream, Authentik, and OpenFGA databases plus OpenBao secrets, with continuous
    WAL archiving for point-in-time recovery. Media files and rebuildable search or queue state are excluded.
  </p>

  {#if startError}
    <div class="alert alert-error mt-5 text-sm" role="alert">
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
      <label class="label mb-2 text-sm" for="backup-type">Type</label>
      <Select id="backup-type" bind:value={backupType} items={backupTypeOptions} />
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
  <p class="mt-2 text-xs text-base-content/50">{backupTypeHints[backupType]}</p>
</section>

<!-- Jobs -->
<section class={cardClass} aria-labelledby="backups-jobs-title">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h2 id="backups-jobs-title" class="text-base font-bold text-base-content">Backup jobs</h2>
      <p class="mt-2 text-sm text-base-content/60">
        Backup, verification, and restore jobs recorded by the backup service.
      </p>
    </div>
    <button class="btn btn-sm btn-neutral" disabled={jobsLoading} onclick={() => void loadJobs(true)}>
      <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </button>
  </div>

  {#if jobsError}
    <div class="alert alert-error mt-5 text-sm" role="alert">
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
      <p class="mt-1 text-sm text-base-content/50">Run a backup or verification to see its progress here.</p>
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
            <span class="badge badge-sm badge-accent text-[10px] font-semibold text-accent-content">
              {describeKind(job)}
            </span>
            {#if job.name}
              <span class="truncate text-xs font-semibold text-base-content/80">{job.name}</span>
            {/if}
            <span class="text-xs text-base-content/60">Started {formatDate(job.createdAt)}</span>
            {#if job.completedAt}
              <span class="text-xs text-base-content/50">· finished {formatDate(job.completedAt)}</span>
            {/if}
            <span class="ml-auto font-mono text-[10px] text-base-content/40">{job.jobId}</span>
          </div>
          {#if job.label}
            <p class="mt-2 truncate font-mono text-xs text-base-content/60" title={job.label}>{job.label}</p>
          {/if}
          {#if job.errorMessage}
            <p class="mt-2 text-xs text-error">{job.errorMessage}</p>
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>

<!-- Repository -->
<section class={cardClass} aria-labelledby="backups-repo-title">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h2 id="backups-repo-title" class="text-base font-bold text-base-content">Backup repository</h2>
      <p class="mt-2 text-sm text-base-content/60">
        Backups in the pgBackRest repository. Quick verify checks every checksum in the repository; deep verify
        test-restores one backup and checks the data inside it.
      </p>
    </div>
    <div class="flex shrink-0 gap-2">
      <button class="btn btn-sm btn-neutral" disabled={quickVerifyBusy} onclick={() => void runQuickVerify()}>
        {#if quickVerifyBusy}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <ShieldCheck class="mr-1.5 h-3.5 w-3.5" />
        {/if}
        Quick verify
      </button>
      <button class="btn btn-sm btn-neutral" disabled={repositoryLoading} onclick={() => void loadRepository()}>
        <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </button>
    </div>
  </div>

  {#if repositoryError}
    <div class="alert alert-error mt-5 text-sm" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{repositoryError}</span>
    </div>
  {/if}
  {#if verifyError}
    <div class="alert alert-error mt-5 text-sm" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{verifyError}</span>
    </div>
  {/if}

  {#if repository}
    <div
      class={[
        'mt-5 flex items-start gap-2 rounded-lg border p-3 text-xs',
        repository.repositoryOk
          ? 'border-info/30 bg-info/10 text-info-content'
          : 'border-error/30 bg-error/10 text-error-content'
      ]}
      role="status"
    >
      {#if repository.repositoryOk}
        <CircleCheck class="mt-0.5 h-4 w-4 shrink-0" />
        <span>
          Repository healthy.
          {#if repository.pitrWindow.earliest}
            Point-in-time recovery covers {formatDate(repository.pitrWindow.earliest)} → now.
          {:else}
            Point-in-time recovery becomes available after the first full backup.
          {/if}
        </span>
      {:else}
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{repository.statusMessage || 'The backup repository is not healthy.'}</span>
      {/if}
    </div>
  {/if}

  {#if repositoryLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if !repository || repository.backups.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <FileArchive class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No backups yet</p>
      <p class="mt-1 text-sm text-base-content/50">Completed backups appear here once written to the repository.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each [...repository.backups].reverse() as backup (backup.label)}
        <article class="rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 sm:px-4">
          <div class="flex flex-col gap-3 sm:flex-row sm:items-center">
            <div class="flex min-w-0 items-center gap-3">
              <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
                <FileArchive class="h-4.5 w-4.5" />
              </span>
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 class="truncate font-mono text-sm font-semibold text-base-content" title={backup.label}>
                    {backup.label}
                  </h3>
                  <span class="badge badge-sm badge-accent text-[10px] font-semibold text-accent-content">
                    {backup.type}
                  </span>
                  {#if backup.name}
                    <span class="badge badge-sm badge-neutral text-[10px] font-semibold">{backup.name}</span>
                  {/if}
                  {#if backup.hasError}
                    <span class="badge badge-sm badge-error text-[10px] font-semibold text-error-content">error</span>
                  {/if}
                  {#if !backup.openBaoExportPresent}
                    <span class="badge badge-sm badge-warning text-[10px] font-semibold text-warning-content">
                      no secrets export
                    </span>
                  {/if}
                </div>
                <p class="mt-0.5 truncate text-xs text-base-content/60">
                  Completed {formatDate(backup.completedAt)} · {formatSize(backup.databaseSize)} database ·
                  {formatSize(backup.repositorySize)} in repo
                  {#if backup.walStart}
                    · WAL {backup.walStart}…{backup.walStop}
                  {/if}
                </p>
              </div>
            </div>

            <div class="flex shrink-0 flex-wrap gap-2 sm:ml-auto">
              <button
                type="button"
                class={rowActionClass}
                disabled={deepVerifyBusyLabel === backup.label}
                onclick={() => void runDeepVerify(backup)}
              >
                {#if deepVerifyBusyLabel === backup.label}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <ShieldCheck class="h-4 w-4" />
                {/if}
                Deep verify
              </button>
            </div>
          </div>
        </article>
      {/each}
    </div>
  {/if}
</section>

<!-- Restore -->
<section class={cardClass} aria-labelledby="backups-restore-title">
  <div class="flex items-start gap-3">
    <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
      <LifeBuoy class="h-4.5 w-4.5" />
    </span>
    <div>
      <h2 id="backups-restore-title" class="text-base font-bold text-base-content">Restore</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Restores run from the standalone restore console, which works while the rest of FrostStream (including sign-in)
        is down. Stop the <code class="font-mono text-xs">postgres</code> container first, then open the console and
        follow the guided steps — it supports restoring a specific backup or point-in-time recovery to any moment in
        the window shown above. The console is protected by the deployment's restore token.
      </p>
      <a class="btn btn-sm btn-neutral mt-4 text-xs" href={restoreUiUrl} target="_blank" rel="noreferrer noopener">
        Open the restore console
      </a>
    </div>
  </div>
</section>
