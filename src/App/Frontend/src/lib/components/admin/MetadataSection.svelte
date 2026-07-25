<script lang="ts">
  import { onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    CircleAlert,
    CircleCheck,
    FileSearch,
    RefreshCw,
    Repeat,
    Trash2,
    Undo2
  } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
  import {
    deleteMedia,
    deleteMediaForStorageKey,
    getOrphanCleanupPolicy,
    listOrphans,
    orphanKindLabel,
    orphanStateLabel,
    restoreOrphanFile,
    restoreOrphanMetadata,
    triggerReindex,
    updateOrphanCleanupPolicy,
    type OrphanCleanupItem,
    type OrphanCleanupPolicy,
    type OrphanKind,
    type OrphanState
  } from '$lib/api/metadata';


  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';

  const ORPHAN_PAGE_SIZE = 25;

  // Search reindex
  let reindexBusy = $state(false);
  let reindexMessage = $state<string | null>(null);
  let reindexError = $state<string | null>(null);

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

<UnderDevelopmentBanner />

<!-- Search index -->
<section class={cardClass} aria-labelledby="metadata-search-title">
  <h2 id="metadata-search-title" class="text-base font-bold text-base-content">Search index</h2>
  <p class="mt-2 text-sm text-base-content/60">
    Rebuild the derived search index from authoritative metadata records. Runs as a background job.
  </p>

  {#if reindexError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{reindexError}</span>
    </div>
  {/if}
  {#if reindexMessage}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-success/30 bg-success/10 p-3 text-sm text-success">
      <CircleCheck class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{reindexMessage}</span>
    </div>
  {/if}

  <div class="mt-4">
    <button class="btn btn-sm btn-neutral" disabled={reindexBusy} onclick={rebuildSearchIndex}>
      {#if reindexBusy}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Repeat class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Rebuild search index
    </button>
  </div>
</section>

<!-- Orphan cleanup policy -->
<section class={cardClass} aria-labelledby="metadata-orphan-policy-title">
  <h2 id="metadata-orphan-policy-title" class="text-base font-bold text-base-content">Orphan cleanup policy</h2>
  <p class="mt-2 text-sm text-base-content/60">
    Controls what happens to files with no metadata and metadata with no file. Destructive cleanup only runs while
    this policy is enabled.
  </p>

  {#if orphanPolicyError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{orphanPolicyError}</span>
    </div>
  {/if}

  {#if orphanPolicyLoading}
    <div class="mt-8 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else}
    <div class="mt-5 space-y-4">
      <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle" bind:checked={orphanEnabled} /><span>Enable automatic orphan cleanup</span></label>

      <div class="grid gap-4 sm:grid-cols-3">
        <div>
          <label class="label mb-2 text-sm" for="orphan-move-days">Move file after (days)</label>
          <input class="input w-full" id="orphan-move-days" type="number" min={1} bind:value={orphanFileMoveDays} />
          <p class="mt-1.5 text-xs text-base-content/50">Days before a file with no metadata is moved to the orphaned folder.</p>
        </div>
        <div>
          <label class="label mb-2 text-sm" for="orphan-purge-days">Purge file after (days)</label>
          <input class="input w-full" id="orphan-purge-days" type="number" min={1} bind:value={orphanFilePurgeDays} />
          <p class="mt-1.5 text-xs text-base-content/50">Days a moved file is kept before permanent deletion.</p>
        </div>
        <div>
          <label class="label mb-2 text-sm" for="orphan-metadata-days">Delete metadata after (days)</label>
          <input class="input w-full" id="orphan-metadata-days" type="number" min={1} bind:value={orphanMetadataDeleteDays} />
          <p class="mt-1.5 text-xs text-base-content/50">Days before metadata whose file is missing is deleted.</p>
        </div>
      </div>

      {#if orphanPolicy}
        <dl class="grid gap-x-6 gap-y-1 text-xs text-base-content/50 sm:grid-cols-2">
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
        <button class="btn btn-sm btn-primary" disabled={orphanPolicySaving} onclick={saveOrphanPolicy}>
          {#if orphanPolicySaving}
            <span class="loading loading-spinner loading-xs mr-1.5"></span>
          {/if}
          Save policy
        </button>
        {#if orphanPolicySaved}
          <span class="inline-flex items-center gap-1 text-xs font-semibold text-success">
            <CircleCheck class="h-3.5 w-3.5" />
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
      <h2 id="metadata-orphans-title" class="text-base font-bold text-base-content">Orphan items</h2>
      <p class="mt-2 text-sm text-base-content/60">
        Files and metadata flagged by filesystem reconciliation. Restore items before cleanup finalizes them.
      </p>
    </div>
    <button class="btn btn-sm btn-neutral" disabled={orphansLoading} onclick={() => void loadOrphans()}>
      <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </button>
  </div>

  <div class="mt-4 grid gap-3 sm:grid-cols-2 lg:max-w-xl">
    <Select items={orphanKindOptions} bind:value={orphanKindFilter} aria-label="Filter by orphan kind" onchange={applyOrphanFilters} />
    <Select items={orphanStateOptions} bind:value={orphanStateFilter} aria-label="Filter by orphan state" onchange={applyOrphanFilters} />
  </div>

  {#if orphansError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{orphansError}</span>
    </div>
  {/if}

  {#if orphansLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if orphans.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <FileSearch class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">
        {orphanPage > 1 ? 'No more orphan items' : 'No orphan items'}
      </p>
      <p class="mt-1 text-sm text-base-content/50">
        {orphanPage > 1 ? 'You have paged past the last result.' : 'Reconciliation has not flagged anything.'}
      </p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each orphans as item (item.id)}
        <article
          class="flex flex-col gap-3 rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="min-w-0">
            <div class="flex min-w-0 flex-wrap items-center gap-2">
              <h3 class="text-sm font-semibold text-base-content">{orphanKindLabel(item.kind)}</h3>
              <span class="rounded-full bg-base-300 px-2 py-0.5 text-[10px] font-semibold text-base-content/60">
                {orphanStateLabel(item.state)}
              </span>
              <span class="rounded-full bg-base-300 px-2 py-0.5 text-[10px] font-semibold text-base-content/60">
                {item.storageKey}
              </span>
            </div>
            <p class="mt-0.5 truncate font-mono text-xs text-base-content/60" title={item.originalStoragePath}>
              {item.originalStoragePath}
            </p>
            <p class="mt-0.5 text-xs text-base-content/50">
              Detected {formatDate(item.detectedAt)} · scheduled for cleanup {formatDate(item.deleteAfter)}
            </p>
            {#if item.lastError}
              <p class="mt-0.5 truncate text-xs text-error" title={item.lastError}>{item.lastError}</p>
            {/if}
          </div>

          {#if canRestore(item)}
            <div class="flex shrink-0 gap-2 sm:ml-auto">
              <button
                type="button"
                class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary disabled:opacity-50"
                disabled={restoringId === item.id}
                onclick={() => void restoreOrphan(item)}
              >
                {#if restoringId === item.id}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <Undo2 class="h-4 w-4" />
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
    <button class="btn btn-sm btn-neutral" disabled={orphansLoading || orphanPage <= 1} onclick={() => changeOrphanPage(-1)}>
      Previous
    </button>
    <span class="text-xs font-semibold text-base-content/50">Page {orphanPage}</span>
    <button class="btn btn-sm btn-neutral" disabled={orphansLoading || orphans.length < ORPHAN_PAGE_SIZE} onclick={() => changeOrphanPage(1)}>
      Next
    </button>
  </div>
</section>

<!-- Delete media -->
<section class="rounded-2xl border border-error/30 bg-base-100 p-5 shadow-xl shadow-black/15 sm:p-6" aria-labelledby="metadata-delete-title">
  <h2 id="metadata-delete-title" class="text-base font-bold text-base-content">Delete a video</h2>
  <p class="mt-2 text-sm text-base-content/60">
    Permanently delete a video by its GUID. Leave the storage key empty to remove every copy, its metadata, and search
    entries; set it to delete only that storage target's copy.
  </p>

  {#if deleteError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{deleteError}</span>
    </div>
  {/if}
  {#if deleteMessage}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-success/30 bg-success/10 p-3 text-sm text-success">
      <CircleCheck class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{deleteMessage}</span>
    </div>
  {/if}

  <div class="mt-4 grid gap-4 sm:grid-cols-2">
    <div>
      <label class="label mb-2 text-sm" for="delete-media-guid">Media GUID</label>
      <input class="input w-full" id="delete-media-guid" bind:value={deleteGuid} placeholder="00000000-0000-0000-0000-000000000000" />
    </div>
    <div>
      <label class="label mb-2 text-sm" for="delete-media-storage-key">Storage key (optional)</label>
      <input class="input w-full" id="delete-media-storage-key" bind:value={deleteStorageKey} placeholder="All storage targets" />
    </div>
  </div>

  <div class="mt-4">
    <button class="btn btn-sm btn-error" disabled={deleteBusy || !deleteGuidValid} onclick={() => (deleteModalOpen = true)}>
      {#if deleteBusy}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Trash2 class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Delete video
    </button>
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
