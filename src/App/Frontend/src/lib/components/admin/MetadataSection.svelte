<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Label, Select, Spinner, Toggle } from 'flowbite-svelte';
  import {
    ArrowsRepeatOutline,
    CheckCircleOutline,
    ExclamationCircleOutline,
    FileSearchOutline,
    PlayOutline,
    RefreshOutline,
    TrashBinOutline,
    UndoOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    deleteMedia,
    deleteMediaForStorageKey,
    getOrphanCleanupPolicy,
    getWatchedAutoDeletePolicy,
    listOrphans,
    orphanKindLabel,
    orphanStateLabel,
    restoreOrphanFile,
    restoreOrphanMetadata,
    runWatchedAutoDelete,
    triggerReindex,
    updateOrphanCleanupPolicy,
    updateWatchedAutoDeletePolicy,
    type OrphanCleanupItem,
    type OrphanCleanupPolicy,
    type OrphanKind,
    type OrphanState,
    type WatchedAutoDeleteCleanupResult,
    type WatchedAutoDeletePolicy
  } from '$lib/api/metadata';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';

  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const saveButtonClass = 'border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';

  const ORPHAN_PAGE_SIZE = 25;

  // Search reindex
  let reindexBusy = $state(false);
  let reindexMessage = $state<string | null>(null);
  let reindexError = $state<string | null>(null);

  // Watched auto-delete policy
  let watchedPolicy = $state<WatchedAutoDeletePolicy | null>(null);
  let watchedLoading = $state(true);
  let watchedError = $state<string | null>(null);
  let watchedSaved = $state(false);
  let watchedEnabled = $state(false);
  let watchedDeleteAfterDays = $state<number | string>(30);
  let watchedMaxPerRun = $state<number | string>(100);
  let watchedSaving = $state(false);
  let watchedRunBusy = $state(false);
  let watchedRunResult = $state<WatchedAutoDeleteCleanupResult | null>(null);

  // Orphan cleanup policy
  let orphanPolicy = $state<OrphanCleanupPolicy | null>(null);
  let orphanPolicyLoading = $state(true);
  let orphanPolicyError = $state<string | null>(null);
  let orphanPolicySaved = $state(false);
  let orphanEnabled = $state(false);
  let orphanFileMoveDays = $state<number | string>(7);
  let orphanFilePurgeDays = $state<number | string>(30);
  let orphanMetadataDeleteDays = $state<number | string>(30);
  let orphanPolicySaving = $state(false);

  // Orphan items
  let orphans = $state<OrphanCleanupItem[]>([]);
  let orphansLoading = $state(true);
  let orphansError = $state<string | null>(null);
  let orphanKindFilter = $state('');
  let orphanStateFilter = $state('');
  let orphanPage = $state(1);
  let restoringId = $state<number | null>(null);

  const orphanKindOptions = [
    { value: '', name: 'All kinds' },
    { value: 'media_without_metadata', name: 'Orphaned files' },
    { value: 'metadata_without_media', name: 'Orphaned metadata' }
  ];

  const orphanStateOptions = [
    { value: '', name: 'All states' },
    { value: 'detected', name: 'Detected' },
    { value: 'moved', name: 'Moved' },
    { value: 'move_failed', name: 'Move failed' },
    { value: 'delete_failed', name: 'Delete failed' },
    { value: 'finalized', name: 'Finalized' },
    { value: 'resolved', name: 'Resolved' }
  ];

  // Delete media
  let deleteGuid = $state('');
  let deleteStorageKey = $state('');
  let deleteBusy = $state(false);
  let deleteError = $state<string | null>(null);
  let deleteMessage = $state<string | null>(null);
  let deleteModalOpen = $state(false);

  const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const deleteGuidValid = $derived(GUID_PATTERN.test(deleteGuid.trim()));

  onMount(() => {
    void loadWatchedPolicy();
    void loadOrphanPolicy();
    void loadOrphans();
  });

  function formatDate(value: string | null): string {
    return value ? new Date(value).toLocaleString() : '—';
  }

  async function rebuildSearchIndex() {
    reindexBusy = true;
    reindexMessage = null;
    reindexError = null;
    try {
      await triggerReindex();
      reindexMessage = 'Reindex queued. The search index rebuilds in the background.';
    } catch (err) {
      reindexError = err instanceof Error ? err.message : 'Could not queue the reindex.';
    } finally {
      reindexBusy = false;
    }
  }

  async function loadWatchedPolicy() {
    watchedLoading = true;
    watchedError = null;
    try {
      applyWatchedPolicy(await getWatchedAutoDeletePolicy());
    } catch (err) {
      watchedError = err instanceof Error ? err.message : 'Could not load the watched auto-delete policy.';
    } finally {
      watchedLoading = false;
    }
  }

  function applyWatchedPolicy(policy: WatchedAutoDeletePolicy) {
    watchedPolicy = policy;
    watchedEnabled = policy.enabled;
    watchedDeleteAfterDays = policy.deleteAfterDays;
    watchedMaxPerRun = policy.maxDeletionsPerRun;
  }

  async function saveWatchedPolicy() {
    watchedSaving = true;
    watchedError = null;
    watchedSaved = false;
    try {
      applyWatchedPolicy(
        await updateWatchedAutoDeletePolicy({
          enabled: watchedEnabled,
          deleteAfterDays: Number(watchedDeleteAfterDays),
          maxDeletionsPerRun: Number(watchedMaxPerRun)
        })
      );
      watchedSaved = true;
    } catch (err) {
      watchedError = err instanceof Error ? err.message : 'Could not save the watched auto-delete policy.';
    } finally {
      watchedSaving = false;
    }
  }

  async function runWatchedCleanupNow() {
    watchedRunBusy = true;
    watchedError = null;
    watchedRunResult = null;
    try {
      watchedRunResult = await runWatchedAutoDelete();
      await loadWatchedPolicy();
    } catch (err) {
      watchedError = err instanceof Error ? err.message : 'Watched auto-delete cleanup failed.';
    } finally {
      watchedRunBusy = false;
    }
  }

  async function loadOrphanPolicy() {
    orphanPolicyLoading = true;
    orphanPolicyError = null;
    try {
      applyOrphanPolicy(await getOrphanCleanupPolicy());
    } catch (err) {
      orphanPolicyError = err instanceof Error ? err.message : 'Could not load the orphan cleanup policy.';
    } finally {
      orphanPolicyLoading = false;
    }
  }

  function applyOrphanPolicy(policy: OrphanCleanupPolicy) {
    orphanPolicy = policy;
    orphanEnabled = policy.enabled;
    orphanFileMoveDays = policy.fileMoveAfterDays;
    orphanFilePurgeDays = policy.filePurgeAfterDays;
    orphanMetadataDeleteDays = policy.metadataDeleteAfterDays;
  }

  async function saveOrphanPolicy() {
    orphanPolicySaving = true;
    orphanPolicyError = null;
    orphanPolicySaved = false;
    try {
      applyOrphanPolicy(
        await updateOrphanCleanupPolicy({
          enabled: orphanEnabled,
          fileMoveAfterDays: Number(orphanFileMoveDays),
          filePurgeAfterDays: Number(orphanFilePurgeDays),
          metadataDeleteAfterDays: Number(orphanMetadataDeleteDays)
        })
      );
      orphanPolicySaved = true;
    } catch (err) {
      orphanPolicyError = err instanceof Error ? err.message : 'Could not save the orphan cleanup policy.';
    } finally {
      orphanPolicySaving = false;
    }
  }

  async function loadOrphans() {
    orphansLoading = true;
    orphansError = null;
    try {
      orphans = await listOrphans({
        kind: (orphanKindFilter || undefined) as OrphanKind | undefined,
        state: (orphanStateFilter || undefined) as OrphanState | undefined,
        pageSize: ORPHAN_PAGE_SIZE,
        page: orphanPage
      });
    } catch (err) {
      orphansError = err instanceof Error ? err.message : 'Could not load orphan items.';
    } finally {
      orphansLoading = false;
    }
  }

  function applyOrphanFilters() {
    orphanPage = 1;
    void loadOrphans();
  }

  function changeOrphanPage(delta: number) {
    orphanPage = Math.max(1, orphanPage + delta);
    void loadOrphans();
  }

  function canRestore(item: OrphanCleanupItem): boolean {
    return item.state !== 'resolved' && item.state !== 'finalized';
  }

  async function restoreOrphan(item: OrphanCleanupItem) {
    restoringId = item.id;
    orphansError = null;
    try {
      if (item.kind === 'media_without_metadata') {
        await restoreOrphanFile(item.id);
      } else {
        await restoreOrphanMetadata(item.id);
      }
      await loadOrphans();
    } catch (err) {
      orphansError = err instanceof Error ? err.message : 'Could not restore the orphan item.';
    } finally {
      restoringId = null;
    }
  }

  async function removeMedia() {
    deleteBusy = true;
    deleteError = null;
    deleteMessage = null;
    try {
      const guid = deleteGuid.trim();
      const storageKey = deleteStorageKey.trim();
      const result = storageKey ? await deleteMediaForStorageKey(guid, storageKey) : await deleteMedia(guid);
      deleteMessage = result.mediaRemoved
        ? `Deleted ${result.filesDeleted} file(s); the video and its metadata were removed.`
        : `Deleted ${result.filesDeleted} file(s) on "${storageKey}"; other copies remain.`;
      deleteGuid = '';
      deleteStorageKey = '';
    } catch (err) {
      deleteError = err instanceof Error ? err.message : 'Could not delete the video.';
      throw err;
    } finally {
      deleteBusy = false;
    }
  }
</script>

<!-- Search index -->
<section class={cardClass} aria-labelledby="metadata-search-title">
  <h2 id="metadata-search-title" class="text-base font-bold text-slate-100">Search index</h2>
  <p class="mt-2 text-sm text-slate-400">
    Rebuild the derived search index from authoritative metadata records. Runs as a background job.
  </p>

  {#if reindexError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{reindexError}</span>
    </div>
  {/if}
  {#if reindexMessage}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-emerald-900/60 bg-emerald-950/35 p-3 text-sm text-emerald-300">
      <CheckCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{reindexMessage}</span>
    </div>
  {/if}

  <div class="mt-4">
    <Button color="dark" class={outlineButtonClass} disabled={reindexBusy} onclick={rebuildSearchIndex}>
      {#if reindexBusy}
        <Spinner size="4" class="mr-1.5" />
      {:else}
        <ArrowsRepeatOutline class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Rebuild search index
    </Button>
  </div>
</section>

<!-- Watched auto-delete policy -->
<section class={cardClass} aria-labelledby="metadata-watched-title">
  <h2 id="metadata-watched-title" class="text-base font-bold text-slate-100">Watched auto-delete</h2>
  <p class="mt-2 text-sm text-slate-400">
    Automatically delete videos after they have been watched. Deletion removes stored files, metadata, and search
    entries.
  </p>

  {#if watchedError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{watchedError}</span>
    </div>
  {/if}

  {#if watchedLoading}
    <div class="mt-8 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else}
    <div class="mt-5 space-y-4">
      <Toggle bind:checked={watchedEnabled} class="text-sm font-medium text-slate-300">
        Enable automatic deletion of watched videos
      </Toggle>

      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <Label for="watched-delete-after" class="mb-2 text-sm font-medium text-slate-300">Delete after (days)</Label>
          <Input id="watched-delete-after" type="number" min={1} bind:value={watchedDeleteAfterDays} class={inputClass} />
        </div>
        <div>
          <Label for="watched-max-per-run" class="mb-2 text-sm font-medium text-slate-300">Max deletions per run</Label>
          <Input id="watched-max-per-run" type="number" min={1} bind:value={watchedMaxPerRun} class={inputClass} />
        </div>
      </div>

      {#if watchedPolicy}
        <dl class="grid gap-x-6 gap-y-1 text-xs text-slate-500 sm:grid-cols-2">
          <div class="flex gap-1.5">
            <dt class="font-semibold">Last run:</dt>
            <dd>{formatDate(watchedPolicy.lastRunAt)}</dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="font-semibold">Last run deleted:</dt>
            <dd>{watchedPolicy.lastDeletedCount} deleted · {watchedPolicy.lastFailedCount} failed</dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="font-semibold">Updated:</dt>
            <dd>{formatDate(watchedPolicy.updatedAt)}{watchedPolicy.updatedBy ? ` by ${watchedPolicy.updatedBy}` : ''}</dd>
          </div>
        </dl>
      {/if}

      {#if watchedRunResult}
        <div class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-3 text-xs text-slate-400">
          Cleanup finished: {watchedRunResult.candidatesFound} candidate(s), {watchedRunResult.deletedCount} deleted,
          {watchedRunResult.failedCount} failed, {watchedRunResult.filesDeleted} file(s) removed.
          {#if !watchedRunResult.policyEnabled}
            The policy is disabled, so nothing was deleted.
          {/if}
        </div>
      {/if}

      <div class="flex flex-wrap items-center gap-2">
        <Button color="blue" class={saveButtonClass} disabled={watchedSaving} onclick={saveWatchedPolicy}>
          {#if watchedSaving}
            <Spinner size="4" class="mr-1.5" />
          {/if}
          Save policy
        </Button>
        <Button color="dark" class={outlineButtonClass} disabled={watchedRunBusy} onclick={runWatchedCleanupNow}>
          {#if watchedRunBusy}
            <Spinner size="4" class="mr-1.5" />
          {:else}
            <PlayOutline class="mr-1.5 h-3.5 w-3.5" />
          {/if}
          Run cleanup now
        </Button>
        {#if watchedSaved}
          <span class="inline-flex items-center gap-1 text-xs font-semibold text-emerald-400">
            <CheckCircleOutline class="h-3.5 w-3.5" />
            Saved
          </span>
        {/if}
      </div>
    </div>
  {/if}
</section>

<!-- Orphan cleanup policy -->
<section class={cardClass} aria-labelledby="metadata-orphan-policy-title">
  <h2 id="metadata-orphan-policy-title" class="text-base font-bold text-slate-100">Orphan cleanup policy</h2>
  <p class="mt-2 text-sm text-slate-400">
    Controls what happens to files with no metadata and metadata with no file. Destructive cleanup only runs while
    this policy is enabled.
  </p>

  {#if orphanPolicyError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{orphanPolicyError}</span>
    </div>
  {/if}

  {#if orphanPolicyLoading}
    <div class="mt-8 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else}
    <div class="mt-5 space-y-4">
      <Toggle bind:checked={orphanEnabled} class="text-sm font-medium text-slate-300">
        Enable automatic orphan cleanup
      </Toggle>

      <div class="grid gap-4 sm:grid-cols-3">
        <div>
          <Label for="orphan-move-days" class="mb-2 text-sm font-medium text-slate-300">Move file after (days)</Label>
          <Input id="orphan-move-days" type="number" min={1} bind:value={orphanFileMoveDays} class={inputClass} />
          <p class="mt-1.5 text-xs text-slate-500">Days before a file with no metadata is moved to the orphaned folder.</p>
        </div>
        <div>
          <Label for="orphan-purge-days" class="mb-2 text-sm font-medium text-slate-300">Purge file after (days)</Label>
          <Input id="orphan-purge-days" type="number" min={1} bind:value={orphanFilePurgeDays} class={inputClass} />
          <p class="mt-1.5 text-xs text-slate-500">Days a moved file is kept before permanent deletion.</p>
        </div>
        <div>
          <Label for="orphan-metadata-days" class="mb-2 text-sm font-medium text-slate-300">Delete metadata after (days)</Label>
          <Input id="orphan-metadata-days" type="number" min={1} bind:value={orphanMetadataDeleteDays} class={inputClass} />
          <p class="mt-1.5 text-xs text-slate-500">Days before metadata whose file is missing is deleted.</p>
        </div>
      </div>

      {#if orphanPolicy}
        <dl class="grid gap-x-6 gap-y-1 text-xs text-slate-500 sm:grid-cols-2">
          <div class="flex gap-1.5">
            <dt class="font-semibold">Last run:</dt>
            <dd>{formatDate(orphanPolicy.lastRunAt)}</dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="font-semibold">Last run counts:</dt>
            <dd>
              {orphanPolicy.lastMovedCount} moved · {orphanPolicy.lastDeletedFilesCount} files deleted ·
              {orphanPolicy.lastDeletedMetadataCount} metadata deleted
            </dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="font-semibold">Updated:</dt>
            <dd>{formatDate(orphanPolicy.updatedAt)}{orphanPolicy.updatedBy ? ` by ${orphanPolicy.updatedBy}` : ''}</dd>
          </div>
        </dl>
      {/if}

      <div class="flex flex-wrap items-center gap-2">
        <Button color="blue" class={saveButtonClass} disabled={orphanPolicySaving} onclick={saveOrphanPolicy}>
          {#if orphanPolicySaving}
            <Spinner size="4" class="mr-1.5" />
          {/if}
          Save policy
        </Button>
        {#if orphanPolicySaved}
          <span class="inline-flex items-center gap-1 text-xs font-semibold text-emerald-400">
            <CheckCircleOutline class="h-3.5 w-3.5" />
            Saved
          </span>
        {/if}
      </div>
    </div>
  {/if}
</section>

<!-- Orphan items -->
<section class={cardClass} aria-labelledby="metadata-orphans-title">
  <div class="flex flex-wrap items-center justify-between gap-3">
    <div>
      <h2 id="metadata-orphans-title" class="text-base font-bold text-slate-100">Orphan items</h2>
      <p class="mt-2 text-sm text-slate-400">
        Files and metadata flagged by filesystem reconciliation. Restore items before cleanup finalizes them.
      </p>
    </div>
    <Button color="dark" class={outlineButtonClass} disabled={orphansLoading} onclick={() => void loadOrphans()}>
      <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </Button>
  </div>

  <div class="mt-4 grid gap-3 sm:grid-cols-2 lg:max-w-xl">
    <Select
      items={orphanKindOptions}
      bind:value={orphanKindFilter}
      class={inputClass}
      aria-label="Filter by orphan kind"
      onchange={applyOrphanFilters}
    />
    <Select
      items={orphanStateOptions}
      bind:value={orphanStateFilter}
      class={inputClass}
      aria-label="Filter by orphan state"
      onchange={applyOrphanFilters}
    />
  </div>

  {#if orphansError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{orphansError}</span>
    </div>
  {/if}

  {#if orphansLoading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if orphans.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <FileSearchOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">
        {orphanPage > 1 ? 'No more orphan items' : 'No orphan items'}
      </p>
      <p class="mt-1 text-sm text-slate-500">
        {orphanPage > 1 ? 'You have paged past the last result.' : 'Reconciliation has not flagged anything.'}
      </p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each orphans as item (item.id)}
        <article
          class="flex flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="min-w-0">
            <div class="flex min-w-0 flex-wrap items-center gap-2">
              <h3 class="text-sm font-semibold text-slate-100">{orphanKindLabel(item.kind)}</h3>
              <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                {orphanStateLabel(item.state)}
              </span>
              <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                {item.storageKey}
              </span>
            </div>
            <p class="mt-0.5 truncate font-mono text-xs text-slate-400" title={item.originalStoragePath}>
              {item.originalStoragePath}
            </p>
            <p class="mt-0.5 text-xs text-slate-500">
              Detected {formatDate(item.detectedAt)} · scheduled for cleanup {formatDate(item.deleteAfter)}
            </p>
            {#if item.lastError}
              <p class="mt-0.5 truncate text-xs text-red-400" title={item.lastError}>{item.lastError}</p>
            {/if}
          </div>

          {#if canRestore(item)}
            <div class="flex shrink-0 gap-2 sm:ml-auto">
              <button
                type="button"
                class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-50"
                disabled={restoringId === item.id}
                onclick={() => void restoreOrphan(item)}
              >
                {#if restoringId === item.id}
                  <Spinner size="4" />
                {:else}
                  <UndoOutline class="h-4 w-4" />
                {/if}
                {item.kind === 'media_without_metadata' ? 'Restore file' : 'Restore metadata'}
              </button>
            </div>
          {/if}
        </article>
      {/each}
    </div>
  {/if}

  <div class="mt-4 flex items-center justify-between">
    <Button
      color="dark"
      class={outlineButtonClass}
      disabled={orphansLoading || orphanPage <= 1}
      onclick={() => changeOrphanPage(-1)}
    >
      Previous
    </Button>
    <span class="text-xs font-semibold text-slate-500">Page {orphanPage}</span>
    <Button
      color="dark"
      class={outlineButtonClass}
      disabled={orphansLoading || orphans.length < ORPHAN_PAGE_SIZE}
      onclick={() => changeOrphanPage(1)}
    >
      Next
    </Button>
  </div>
</section>

<!-- Delete media -->
<section class="rounded-2xl border border-red-900/40 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6" aria-labelledby="metadata-delete-title">
  <h2 id="metadata-delete-title" class="text-base font-bold text-slate-100">Delete a video</h2>
  <p class="mt-2 text-sm text-slate-400">
    Permanently delete a video by its GUID. Leave the storage key empty to remove every copy, its metadata, and search
    entries; set it to delete only that storage target's copy.
  </p>

  {#if deleteError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{deleteError}</span>
    </div>
  {/if}
  {#if deleteMessage}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-emerald-900/60 bg-emerald-950/35 p-3 text-sm text-emerald-300">
      <CheckCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{deleteMessage}</span>
    </div>
  {/if}

  <div class="mt-4 grid gap-4 sm:grid-cols-2">
    <div>
      <Label for="delete-media-guid" class="mb-2 text-sm font-medium text-slate-300">Media GUID</Label>
      <Input
        id="delete-media-guid"
        bind:value={deleteGuid}
        placeholder="00000000-0000-0000-0000-000000000000"
        class={inputClass}
      />
    </div>
    <div>
      <Label for="delete-media-storage-key" class="mb-2 text-sm font-medium text-slate-300">Storage key (optional)</Label>
      <Input id="delete-media-storage-key" bind:value={deleteStorageKey} placeholder="All storage targets" class={inputClass} />
    </div>
  </div>

  <div class="mt-4">
    <Button
      color="red"
      class={saveButtonClass}
      disabled={deleteBusy || !deleteGuidValid}
      onclick={() => (deleteModalOpen = true)}
    >
      {#if deleteBusy}
        <Spinner size="4" class="mr-1.5" />
      {:else}
        <TrashBinOutline class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Delete video
    </Button>
  </div>
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete video"
  message={deleteStorageKey.trim()
    ? `Delete the copy of video ${deleteGuid.trim()} stored on "${deleteStorageKey.trim()}"? If it is the last copy, the video's metadata and search entries are removed too.`
    : `Permanently delete video ${deleteGuid.trim()}? Every stored copy, its metadata, and search entries will be removed.`}
  confirmLabel="Delete video"
  onConfirm={removeMedia}
/>
