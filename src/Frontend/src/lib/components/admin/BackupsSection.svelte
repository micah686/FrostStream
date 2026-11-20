<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { page } from '$app/state';
  const capabilities = $derived(page.data.capabilities.backups);
  const lite = $derived(page.data.lite);
  const deepVerification = $derived(capabilities.deepVerification ?? capabilities.pointInTimeRecovery);
  import { Modal, Select } from '#lib/components/ui/index.js';
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
    getBackupHealth,
    runBackupMaintenance,
    listBackupJobs,
    listBackups,
    startBackup,
    verifyBackup,
    type BackupHealth,
    type BackupInfo,
    type BackupJob,
    type BackupRepository,
    type BackupType
  } from '#lib/api/backups.js';

  const backupTypeOptions = $derived([
    { value: 'full', name: 'Full', enabled: capabilities.full },
    { value: 'diff', name: 'Differential', enabled: capabilities.differential },
    { value: 'incr', name: 'Incremental', enabled: capabilities.incremental }
  ].filter((option) => option.enabled));

  const backupTypeNames: Record<BackupType, string> = {
    full: 'Full',
    diff: 'Differential',
    incr: 'Incremental'
  };

  const cardClass = 'card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6';
  const rowActionClass = 'btn btn-sm btn-neutral text-xs';

  const JOB_POLL_INTERVAL_MS = 4000;
  const JOBS_PER_PAGE = 10;

  // Run backup
  let backupName = $state('');
  let backupType = $state<BackupType>('full');
  let startBusy = $state(false);
  let startError = $state<string | null>(null);

  // Jobs
  let jobs = $state<BackupJob[]>([]);
  let jobsLoading = $state(true);
  let jobsError = $state<string | null>(null);
  let jobsPage = $state(0);
  const visibleJobs = $derived(jobs.slice(jobsPage * JOBS_PER_PAGE, (jobsPage + 1) * JOBS_PER_PAGE));
  const jobPageCount = $derived(Math.ceil(jobs.length / JOBS_PER_PAGE));
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  // Repository
  let repository = $state<BackupRepository | null>(null);
  let repositoryLoading = $state(true);
  let repositoryError = $state<string | null>(null);

  // Verify actions
  let quickVerifyBusy = $state(false);
  let deepVerifyBusyLabel = $state<string | null>(null);
  let verifyError = $state<string | null>(null);

  let health = $state<BackupHealth | null>(null);
  let healthError = $state<string | null>(null);
  let maintenanceBusy = $state(false);
  let deepVerifyModalOpen = $state(false);
  let selectedVerifyBackup = $state<BackupInfo | null>(null);
  let backupsPage = $state(0);
  const sortedBackups = $derived([...(repository?.backups ?? [])].reverse());
  const backupPageCount = $derived(Math.ceil(sortedBackups.length / JOBS_PER_PAGE));
  const visibleBackups = $derived(sortedBackups.slice(backupsPage * JOBS_PER_PAGE, (backupsPage + 1) * JOBS_PER_PAGE));

  async function loadHealth() {
    if (lite) return;
    try { health = await getBackupHealth(); healthError = null; }
    catch (err) { healthError = err instanceof Error ? err.message : 'Could not load backup health.'; }
  }

  async function runMaintenance(operation: 'check' | 'inspect-archive') {
    maintenanceBusy = true;
    try { mergeJob(await runBackupMaintenance(operation)); healthError = null; }
    catch (err) { healthError = err instanceof Error ? err.message : 'Could not start maintenance.'; }
    finally { maintenanceBusy = false; }
  }

  const recentRepositoryChecks = $derived([
    { name: 'Repository verification', operation: 'verify' as const, description: lite ? 'Checks the latest SQLite snapshot, its integrity and recovery artifacts.' : 'Quick verify checks backup and archive checksums across the pgBackRest repository. Deep verify on a saved backup also performs a temporary restore.', job: jobs.find((job) => job.kind === 'verify-quick') },
    ...(!lite ? [
      { name: 'Archive check', operation: 'check' as const, description: 'Checks pgBackRest configuration and asks PostgreSQL to archive WAL, confirming the current archive path works.', job: jobs.find((job) => job.kind === 'check') },
      { name: 'WAL inspection', operation: 'inspect-archive' as const, description: 'Retrieves archived WAL segments and timeline history, reporting unreadable files and gaps within the observed range. This can take a long time. Recovery target reachability remains unassessed.', job: jobs.find((job) => job.kind === 'inspect-archive') }
    ] : [])
  ]);
  const backupIssues = $derived(repository?.backups.filter((backup) =>
    backup.hasError || (lite ? backup.keyRingBackupPresent === false : !backup.openBaoExportPresent)).length ?? 0);
  const walState = $derived.by(() => {
    if (!health || healthError || !health.archiverStatistics) return 'Unknown';
    try {
      const stats = JSON.parse(health.archiverStatistics);
      if (typeof stats.failure_unresolved !== 'boolean' || typeof stats.archive_mode !== 'string') return 'Unknown';
      return stats.failure_unresolved || stats.archive_mode === 'off' || stats.archive_timeout === '0'
        ? 'Needs attention' : 'Archiving enabled';
    } catch { return 'Unknown'; }
  });
  const repositoryStatuses = $derived([
    {
      name: 'Overall health',
      description: 'Combines repository availability, recovery artifact issues, free space and PostgreSQL archiver health. Observed ' + (health ? formatDate(health.observedAt) : 'health snapshot pending') + '.',
      state: repositoryError || healthError ? 'Unavailable' : !repository || (!lite && !health) ? 'Unknown'
        : !repository.repositoryOk || backupIssues > 0 || (!lite && !health?.healthy) ? 'Needs attention' : 'Healthy',
      detail: lite ? 'Repository and recovery artifacts' : 'Repository, storage and archiver health'
    },
    {
      name: 'Backups',
      description: 'Shows saved backups and reports backup errors or missing recovery artifacts. Checksum verification results appear in the repository verification card.',
      state: repositoryError || !repository ? 'Unknown' : !repository.repositoryOk || backupIssues > 0 ? 'Needs attention'
        : repository.backups.length ? 'Available' : 'No backups',
      detail: repository ? repository.backups.length + ' saved · ' + backupIssues + ' with reported issues' : 'Waiting for repository'
    },
    ...(!lite ? [{
      name: 'WAL archiving', description: 'Shows current PostgreSQL archiver settings and unresolved failures. The backlog counts WAL segments waiting to be archived.', state: walState,
      detail: health ? health.walReadySegments + ' segments awaiting archival' : 'Waiting for archiver statistics'
    }] : []),
    ...(capabilities.pointInTimeRecovery ? [{
      name: 'PITR coverage', description: 'Point-in-time recovery coverage requires assessment of WAL continuity and recovery target reachability. A recovery window estimate alone leaves coverage unknown.', state: 'Unknown',
      detail: repository?.pitrWindow.earliest ? 'Estimated start ' + formatDate(repository.pitrWindow.earliest)
        : 'Recovery window has not been established'
    }] : [])
  ]);

  function statusColor(state: string) {
    if (['Healthy', 'Available', 'Archiving enabled'].includes(state)) return 'badge-success';
    if (['Needs attention', 'Unavailable'].includes(state)) return 'badge-warning';
    return 'badge-neutral';
  }

  let restoreUiUrl = $state('');

  const hasActiveJobs = $derived(jobs.some((job) => job.status === 'queued' || job.status === 'running'));

  onMount(() => {
    restoreUiUrl = `http://${window.location.hostname}:25900/`;
    void loadJobs(true);
    void loadRepository();
    void loadHealth();
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
      jobsPage = Math.min(jobsPage, Math.max(0, Math.ceil(jobs.length / JOBS_PER_PAGE) - 1));
      jobsError = null;
      if (hasActiveJobs) {
        startPolling();
      } else {
        stopPolling();
        if (previousActive) {
          // A job just finished; the repository may have a new backup or verify result.
          void loadRepository();
          void loadHealth();
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
      backupsPage = Math.min(backupsPage, Math.max(0, Math.ceil(repository.backups.length / JOBS_PER_PAGE) - 1));
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
    if (verificationState(backup).name === 'Processing') return;
    deepVerifyBusyLabel = backup.label;
    verifyError = null;
    try {
      mergeJob(await verifyBackup(backup.label, deepVerification));
      deepVerifyModalOpen = false;
    } catch (err) {
      verifyError = err instanceof Error ? err.message : 'Could not start the deep verification.';
    } finally {
      deepVerifyBusyLabel = null;
    }
  }

  function verificationState(backup: BackupInfo) {
    const checks = jobs.filter((job) => job.label === backup.label &&
      job.kind === (deepVerification ? 'verify-deep' : 'verify-quick'));
    if (deepVerifyBusyLabel === backup.label || checks.some((job) => job.status === 'queued' || job.status === 'running')) {
      return { name: 'Processing', color: 'badge-info', description: 'Verification is queued or running.' };
    }
    if (backup.hasError || checks[0]?.status === 'failed') {
      return { name: 'Invalid', color: 'badge-error', description: checks[0]?.errorMessage ?? 'The backup reports an error.' };
    }
    if (checks[0]?.status === 'completed') {
      return { name: 'OK', color: 'badge-success', description: 'Last verification passed ' + formatDate(checks[0].completedAt) };
    }
    return { name: 'Unknown', color: 'badge-neutral', description: 'No completed verification recorded for this backup.' };
  }

  function mergeJob(job: BackupJob) {
    jobs = [job, ...jobs.filter((item) => item.jobId !== job.jobId)];
    jobsPage = 0;
    if (job.status === 'queued' || job.status === 'running') startPolling();
    else void loadRepository();
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
        return job.type ? `${backupTypeNames[job.type]} backup` : 'Backup';
      case 'verify-quick':
        return 'quick verify';
      case 'verify-deep':
        return 'deep verify';
      case 'inspect-archive':
        return 'archive inspection';
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
    {#if lite}
      Full SQLite snapshots cover application data, workflow and queue state, staged objects, and encrypted secrets.
      Media files and optional ClickHouse data are managed separately.
    {:else}
      pgBackRest backups cover the FrostStream, Authentik, and OpenFGA databases plus OpenBao secrets, with continuous
      WAL archiving for point-in-time recovery. Media files and rebuildable search or queue state are excluded.
    {/if}
  </p>

  {#if repository?.databasePath || repository?.backupDirectory}
    <dl class="mt-3 text-sm text-base-content/70">
      {#if repository.databasePath}<dt class="font-semibold">Database location</dt><dd class="break-all font-mono">{repository.databasePath}</dd>{/if}
      {#if repository.keyRingPath}<dt class="mt-2 font-semibold">Encryption key directory</dt><dd class="break-all font-mono">{repository.keyRingPath}</dd>{/if}
      {#if repository.backupDirectory}<dt class="mt-2 font-semibold">Backup directory</dt><dd class="break-all font-mono">{repository.backupDirectory}</dd>{/if}
    </dl>
  {/if}

  {#if startError}
    <div class="alert alert-error mt-5 text-sm" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{startError}</span>
    </div>
  {/if}

  <form onsubmit={runBackup} class="mt-5 flex flex-col gap-3 sm:flex-row sm:items-end">
    <div class="min-w-0 flex-1 sm:max-w-xs">
      <label class="label mb-2 text-sm" for="backup-name">{lite ? 'Backup name (optional)' : 'Backup name'}</label>
      <input class="input w-full" id="backup-name" maxlength={lite ? 100 : 128} required={!lite} bind:value={backupName} placeholder="pre-upgrade" />
    </div>
    <div class="min-w-0 flex-1 sm:max-w-sm">
      <label class="label mb-2 text-sm" for="backup-type">Type</label>
      <Select id="backup-type" bind:value={backupType} items={backupTypeOptions} />
    </div>
    <button class="btn btn-sm btn-primary text-xs sm:-translate-y-1" type="submit" disabled={startBusy || !capabilities.full}>
      {#if startBusy}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Play class="mr-1.5 h-4 w-4" />
      {/if}
      Run backup now
    </button>
  </form>
  {#if lite}
    <p class="mt-3 text-sm text-base-content/70">Full snapshots include the complete SQLite database.</p>
  {:else}
    <dl class="mt-4 grid gap-2 text-sm text-base-content/70 sm:grid-cols-3">
      {#if capabilities.full}<div><dt class="font-semibold text-base-content">Full</dt><dd>Copies the entire PostgreSQL cluster.</dd></div>{/if}
      {#if capabilities.differential}<div><dt class="font-semibold text-base-content">Differential</dt><dd>Copies changes since the latest full backup.</dd></div>{/if}
      {#if capabilities.incremental}<div><dt class="font-semibold text-base-content">Incremental</dt><dd>Copies changes since the latest backup.</dd></div>{/if}
    </dl>
  {/if}
</section>

<!-- Jobs -->
<section class={cardClass} aria-labelledby="backups-jobs-title">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h2 id="backups-jobs-title" class="text-base font-bold text-base-content">Backup jobs</h2>
      <p class="mt-2 text-sm text-base-content/60">
        {lite ? 'Backup and verification jobs from this server session.' : 'Backup, verification, and restore jobs recorded by the backup service.'}
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
    <div class="mt-5 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/30 p-8 text-center">
      <CloudUpload class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No backup jobs yet</p>
      <p class="mt-1 text-sm text-base-content/50">Run a backup or verification to see its progress here.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each visibleJobs as job (job.jobId)}
        <article class="rounded-field border-[length:var(--border)] border-base-content/20 bg-base-100 px-3 py-3 sm:px-4">
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
            <span class="text-xs text-base-content/60">{job.startedAt ? 'Started' : 'Queued'} {formatDate(job.startedAt ?? job.createdAt)}</span>
            {#if job.completedAt}
              <span class="text-xs text-base-content/50">· finished {formatDate(job.completedAt)}</span>
            {/if}
            <span class="ml-auto font-mono text-[10px] text-base-content/40">{job.jobId}</span>
          </div>
          {#if job.label}
            <p class="mt-2 truncate font-mono text-xs text-base-content/60" title={job.label}>{job.label}</p>
          {/if}
          {#if job.actualType && job.actualType !== job.type}
            <p class="mt-2 text-xs text-base-content/70">Created a {backupTypeNames[job.actualType].toLowerCase()} backup.</p>
          {/if}
          {#if job.errorMessage}
            <p class="mt-2 text-xs text-error">{job.errorMessage}</p>
          {/if}
          {#if job.progress.length}
            <details class="mt-2 text-xs text-base-content/70">
              <summary class="cursor-pointer">Job output</summary>
              <pre class="mt-2 max-h-48 overflow-auto whitespace-pre-wrap break-words">{job.progress.join('\n')}</pre>
            </details>
          {/if}
        </article>
      {/each}
    </div>
    {#if jobPageCount > 1}
      <div class="mt-4 flex justify-end gap-2">
        <button class="btn btn-sm btn-neutral" disabled={jobsPage === 0} onclick={() => jobsPage -= 1}>Previous</button>
        <button class="btn btn-sm btn-neutral" disabled={jobsPage >= jobPageCount - 1} onclick={() => jobsPage += 1}>Next</button>
      </div>
    {/if}
  {/if}
</section>

<!-- Repository -->
<section class={cardClass} aria-labelledby="backups-repo-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 id="backups-repo-title" class="text-base font-bold text-base-content">Backup repository</h2>
      <p class="mt-1 text-sm text-base-content/60">Recovery status and saved backups</p>
    </div>
  </div>

  <div class="mt-5 grid auto-rows-fr gap-3 sm:grid-cols-2 xl:grid-cols-4" aria-live="polite">
    {#each repositoryStatuses as status, index (status.name)}
      <article class="flex min-h-64 min-w-0 flex-col rounded-field border-[length:var(--border)] border-base-300 bg-base-200/30 p-4">
        <div class="flex items-center gap-2">
          <h3 class="text-sm font-semibold text-base-content">{status.name}</h3>
          <div class="group relative">
            <button type="button" class="grid h-5 w-5 place-items-center rounded-full border border-base-content/30 text-xs text-base-content/60 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary" aria-label={'About ' + status.name} aria-describedby={'status-help-' + index}>?</button>
            <span id={'status-help-' + index} role="tooltip" class="invisible absolute right-0 top-7 z-20 w-56 rounded-field border border-base-300 bg-base-100 p-3 text-xs font-normal text-base-content shadow-lg group-hover:visible group-focus-within:visible">{status.description}</span>
          </div>
        </div>
        <div class="mt-3">
          <span class="badge badge-sm {statusColor(status.state)}">{status.state}</span>
          <p class="mt-2 text-xs text-base-content/70">{status.detail}</p>
        </div>
        {#if status.name !== 'PITR coverage'}
          <div class="mt-auto pt-4">
            <button class="btn btn-sm btn-neutral h-auto min-h-9 w-full whitespace-normal px-2 py-2 text-xs" disabled={repositoryLoading} onclick={() => {
              if (status.name !== 'WAL archiving') void loadRepository();
              if (status.name !== 'Backups') void loadHealth();
            }}>
              <RefreshCw class="h-3.5 w-3.5 shrink-0" />Refresh status
            </button>
          </div>
        {/if}
      </article>
    {/each}
    {#each recentRepositoryChecks as check (check.name)}
      <article class="flex min-h-64 min-w-0 flex-col rounded-field border-[length:var(--border)] border-base-300 bg-base-200/30 p-4">
        <div class="flex items-center gap-2">
          <h3 class="text-sm font-semibold text-base-content">{check.name}</h3>
          <div class="group relative">
            <button type="button" class="grid h-5 w-5 place-items-center rounded-full border border-base-content/30 text-xs text-base-content/60 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary" aria-label={'About ' + check.name} aria-describedby={'check-help-' + check.operation}>?</button>
            <span id={'check-help-' + check.operation} role="tooltip" class="invisible absolute right-0 top-7 z-20 w-56 rounded-field border border-base-300 bg-base-100 p-3 text-xs font-normal text-base-content shadow-lg group-hover:visible group-focus-within:visible">{check.description}</span>
          </div>
        </div>
        <div class="mt-3">
          <span class="badge badge-sm {check.job ? statusBadgeClass(check.job.status) : 'badge-neutral'}">{check.job?.status ?? 'Not run'}</span>
          {#if check.job}
            <p class="mt-2 text-xs text-base-content/50">{check.job.completedAt ? 'Finished' : 'Queued'} {formatDate(check.job.completedAt ?? check.job.createdAt)}</p>
            {#if check.job.errorMessage}<p class="mt-2 break-words text-xs text-error">{check.job.errorMessage}</p>{/if}
            <p class="mt-3 break-words text-xs text-base-content/70">
              {check.job.status === 'completed'
                ? check.job.resultSummary ?? 'Check completed. Run it again to get a result summary.'
                : check.job.status === 'running' ? 'Check in progress…'
                : check.job.status === 'queued' ? 'Waiting to start.'
                : 'The check could not complete. Review the error and job output.'}
            </p>
          {:else}
            <p class="mt-2 text-xs text-base-content/50">No check recorded yet.</p>
          {/if}
        </div>
        <div class="mt-auto pt-4">
          {#if check.operation === 'verify'}
            <button class="btn btn-sm btn-neutral h-auto min-h-9 w-full whitespace-normal px-2 py-2 text-xs" disabled={quickVerifyBusy || !capabilities.verification || (lite && !repository?.backups.length)} onclick={() => void runQuickVerify()}>
              {#if quickVerifyBusy}<span class="loading loading-spinner loading-xs"></span>{:else}<ShieldCheck class="h-3.5 w-3.5" />{/if}
              {lite ? 'Verify latest' : 'Quick verify'}
            </button>
          {:else}
            <button class="btn btn-sm btn-neutral h-auto min-h-9 w-full whitespace-normal px-2 py-2 text-xs" disabled={maintenanceBusy || hasActiveJobs} onclick={() => void runMaintenance(check.operation)}>
              {check.operation === 'check' ? 'Check archiving' : 'Inspect archived WAL'}
            </button>
          {/if}
        </div>
      </article>
    {/each}
  </div>

  {#if repositoryError || healthError || verifyError}
    <div class="alert alert-error mt-4 text-sm" role="alert">
      <CircleAlert class="h-4 w-4 shrink-0" />
      <div>
        {#if repositoryError}<p>{repositoryError}</p>{/if}
        {#if healthError}<p>{healthError}</p>{/if}
        {#if verifyError}<p>{verifyError}</p>{/if}
      </div>
    </div>
  {/if}

  {#if repository?.statusMessage || (!lite && health)}
    <details class="mt-4 rounded-field border-[length:var(--border)] border-base-300 p-3 text-xs text-base-content/70">
      <summary class="cursor-pointer font-semibold">Health details</summary>
      {#if repository?.statusMessage}<p class="mt-3">{repository.statusMessage}</p>{/if}
      {#if !lite && health}
        <dl class="mt-3 flex flex-wrap gap-x-6 gap-y-2">
          <div><dt class="text-base-content/50">Repository free space</dt><dd class="mt-1">{formatSize(health.repositoryFreeBytes)}</dd></div>
          <div><dt class="text-base-content/50">Database free space</dt><dd class="mt-1">{formatSize(health.dataFreeBytes)}</dd></div>
        </dl>
        {#if health.problems.length}
          <ul class="mt-3 list-inside list-disc space-y-1 text-warning">
            {#each health.problems as problem}<li>{problem}</li>{/each}
          </ul>
        {/if}
      {/if}
    </details>
  {/if}

  {#if repositoryLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if !repository || repository.backups.length === 0}
    <div class="mt-5 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/30 p-8 text-center">
      <FileArchive class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No backups yet</p>
      <p class="mt-1 text-sm text-base-content/50">Completed backups appear here once written to the repository.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each visibleBackups as backup (backup.label)}
        {@const verification = verificationState(backup)}
        <article class="rounded-field border-[length:var(--border)] border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 sm:px-4">
          <div class="flex flex-col gap-3 sm:flex-row sm:items-center">
            <div class="flex min-w-0 items-center gap-3">
              <span class="grid h-9 w-9 shrink-0 place-items-center rounded-field bg-base-300/70 text-primary">
                <FileArchive class="h-4.5 w-4.5" />
              </span>
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 class="truncate font-mono text-sm font-semibold text-base-content" title={backup.label}>
                    {backup.label}
                  </h3>
                  <span class="badge badge-sm badge-accent text-[10px] font-semibold text-accent-content">
                    {backupTypeNames[backup.type] ?? backup.type}
                  </span>
                  {#if backup.name}
                    <span class="badge badge-sm badge-neutral text-[10px] font-semibold">{backup.name}</span>
                  {/if}
                  {#if backup.hasError}
                    <span class="badge badge-sm badge-error text-[10px] font-semibold text-error-content">error</span>
                  {/if}
                  {#if lite && backup.keyRingBackupPresent === false}
                    <span class="badge badge-sm badge-warning text-[10px] font-semibold text-warning-content">missing recovery keys or manifest</span>
                  {/if}
                  {#if !lite && !backup.openBaoExportPresent}
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

            <div class="flex shrink-0 flex-wrap items-center gap-2 sm:ml-auto">
              <span class="badge badge-sm {verification.color}" title={verification.description} aria-label={'Verification status: ' + verification.name + '. ' + verification.description}>{verification.name}</span>
              <button
                type="button"
                class={rowActionClass}
                disabled={verification.name === 'Processing' || jobsLoading || !!jobsError || !capabilities.verification}
                onclick={() => {
                  if (deepVerification) {
                    selectedVerifyBackup = backup;
                    verifyError = null;
                    deepVerifyModalOpen = true;
                  } else void runDeepVerify(backup);
                }}
              >
                {#if verification.name === 'Processing'}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <ShieldCheck class="h-4 w-4" />
                {/if}
                {deepVerification ? 'Deep verify' : 'Verify'}
              </button>
            </div>
          </div>
        </article>
      {/each}
    </div>
    {#if backupPageCount > 1}
      <div class="mt-4 flex justify-end gap-2">
        <button class="btn btn-sm btn-neutral" disabled={backupsPage === 0} onclick={() => backupsPage -= 1}>Previous</button>
        <button class="btn btn-sm btn-neutral" disabled={backupsPage >= backupPageCount - 1} onclick={() => backupsPage += 1}>Next</button>
      </div>
    {/if}
  {/if}
</section>

<!-- Restore -->
{#if capabilities.pointInTimeRecovery}
<section class={cardClass} aria-labelledby="backups-restore-title">
  <div class="flex items-start gap-3">
    <span class="grid h-9 w-9 shrink-0 place-items-center rounded-field bg-base-300/70 text-primary">
      <LifeBuoy class="h-4.5 w-4.5" />
    </span>
    <div>
      <h2 id="backups-restore-title" class="text-base font-bold text-base-content">Restore</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Restores run from the standalone restore console, which works while the rest of FrostStream (including sign-in)
        is down. Stop the <code class="font-mono text-xs">postgres</code> container first, then open the console and
        follow the guided steps for backup or point-in-time recovery. The console is protected by the deployment's restore token.
      </p>
      <a class="btn btn-sm btn-neutral mt-4 text-xs" href={restoreUiUrl} target="_blank" rel="noreferrer noopener">
        Open the restore console
      </a>
    </div>
  </div>
</section>
{/if}

{#if lite}
<section class={cardClass} aria-labelledby="backups-lite-restore-title">
  <h2 id="backups-lite-restore-title" class="text-base font-bold text-base-content">Restore a SQLite snapshot</h2>
  <ol class="mt-3 list-decimal space-y-2 pl-5 text-sm leading-6 text-base-content/70">
    <li>Verify the selected recovery set above. Keep its <code>.sqlite</code> file, matching <code>.sqlite.keys</code> directory, and <code>.sqlite.recovery.json</code> manifest together.</li>
    <li>Shut down the Lite server and any other process using the database. Preserve the current database, any WAL/SHM files, and the complete current encryption key directory in a separate rollback directory.</li>
    <li>Only after shutdown, move leftover WAL/SHM files out of the live database location. Copy the selected snapshot to the displayed database location (default <code>/data/frostreamlitedb</code>). Copy its companion key directory to the displayed encryption key location, retaining all older keys and owner-only permissions.</li>
    <li>Restart with the same configuration. Check readiness, library data, workflows, schedules, imports, and encrypted storage credentials. Media files, import source files, and optional ClickHouse data are managed separately; rebuild Typesense from the restored application data.</li>
    <li>If validation fails, stop the server again, preserve the failed restored files separately, and put the saved database and complete key ring back. Keep the rollback copy until recovery succeeds.</li>
  </ol>
  <p class="mt-3 text-xs text-base-content/60">Recovery on another installation requires the companion keys. Restored downloads follow normal startup reconciliation; queued or active runs do not automatically resume. Create a new complete snapshot on the original installation before migrating an older database-only backup.</p>
</section>
{/if}

<Modal bind:open={deepVerifyModalOpen} title="Deep verify backup?" size="md">
  <p class="break-all font-mono text-sm text-base-content">{selectedVerifyBackup?.label}</p>
  <p class="mt-3 text-sm text-base-content/80">Verify this backup's checksums, restore it to a temporary directory, and start a temporary PostgreSQL instance to validate recovery. This may take time and requires disk space for the restored database.</p>
  {#if verifyError}<p class="mt-3 text-sm text-error" role="alert">{verifyError}</p>{/if}
  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <button class="btn btn-sm btn-ghost" disabled={deepVerifyBusyLabel !== null} onclick={() => deepVerifyModalOpen = false}>Cancel</button>
      <button class="btn btn-sm btn-primary" disabled={!selectedVerifyBackup || deepVerifyBusyLabel !== null || jobsLoading || !!jobsError || (selectedVerifyBackup && verificationState(selectedVerifyBackup).name === 'Processing')} onclick={() => { if (selectedVerifyBackup) void runDeepVerify(selectedVerifyBackup); }}>
        {#if deepVerifyBusyLabel}<span class="loading loading-spinner loading-xs"></span>{/if}
        Start deep verification
      </button>
    </div>
  {/snippet}
</Modal>
