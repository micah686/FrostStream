<script lang="ts">
  import { CircleAlert, CircleCheck, Database, Repeat, Trash2 } from '@lucide/svelte';
  import { Select } from '$lib/components/ui';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
  import {
    deleteMedia,
    deleteMediaForStorageKey,
    getMetadataVersions,
    triggerDatabaseReindex,
    triggerReindex
  } from '$lib/api/metadata';

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';

  // Search reindex
  let reindexBusy = $state(false);
  let reindexMessage = $state<string | null>(null);
  let reindexError = $state<string | null>(null);

  // Whole-database reindex
  let databaseReindexBusy = $state(false);
  let databaseReindexMessage = $state<string | null>(null);
  let databaseReindexError = $state<string | null>(null);

  // Delete media
  let deleteGuid = $state('');
  let deleteStorageKey = $state('');
  let deleteBusy = $state(false);
  let deleteError = $state<string | null>(null);
  let deleteMessage = $state<string | null>(null);
  let deleteModalOpen = $state(false);

  const ALL_STORAGE_OPTION = { value: '', name: 'All storage targets' };
  let storageKeyOptions = $state([ALL_STORAGE_OPTION]);
  let storageKeysLoading = $state(false);
  let storageKeysError = $state<string | null>(null);
  let deleteGuidExists = $state(false);

  const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const deleteGuidValid = $derived(GUID_PATTERN.test(deleteGuid.trim()));
  const canDelete = $derived(deleteGuidValid && deleteGuidExists && !storageKeysLoading);

  $effect(() => {
    const guid = deleteGuid.trim();
    if (!GUID_PATTERN.test(guid)) {
      storageKeyOptions = [ALL_STORAGE_OPTION];
      storageKeysError = null;
      deleteGuidExists = false;
      deleteStorageKey = '';
      return;
    }

    storageKeysLoading = true;
    storageKeysError = null;
    deleteGuidExists = false;
    getMetadataVersions(guid)
      .then((response) => {
        if (deleteGuid.trim() !== guid) return;
        const keys = Array.from(new Set(response.versions.map((v) => v.storageKey))).sort();
        storageKeyOptions = [ALL_STORAGE_OPTION, ...keys.map((key) => ({ value: key, name: key }))];
        deleteGuidExists = keys.length > 0;
        if (deleteStorageKey && !keys.includes(deleteStorageKey)) {
          deleteStorageKey = '';
        }
      })
      .catch((err) => {
        if (deleteGuid.trim() !== guid) return;
        storageKeysError = err instanceof Error ? err.message : 'Could not load storage keys for this GUID.';
        storageKeyOptions = [ALL_STORAGE_OPTION];
        deleteGuidExists = false;
      })
      .finally(() => {
        if (deleteGuid.trim() === guid) storageKeysLoading = false;
      });
  });

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

  async function reindexDatabase() {
    databaseReindexBusy = true;
    databaseReindexMessage = null;
    databaseReindexError = null;
    try {
      await triggerDatabaseReindex();
      databaseReindexMessage = 'Database reindex queued. PostgreSQL will rebuild indexes concurrently in the background.';
    } catch (err) {
      databaseReindexError = err instanceof Error ? err.message : 'Could not queue the database reindex.';
    } finally {
      databaseReindexBusy = false;
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
        ? `Deleted ${result.filesDeleted} file(s); the media and its metadata were removed.`
        : `Deleted ${result.filesDeleted} file(s) on "${storageKey}"; other copies remain.`;
      deleteGuid = '';
      deleteStorageKey = '';
    } catch (err) {
      deleteError = err instanceof Error ? err.message : 'Could not delete the media.';
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

<!-- Database maintenance -->
<section class={cardClass} aria-labelledby="metadata-database-reindex-title">
  <h2 id="metadata-database-reindex-title" class="text-base font-bold text-base-content">Database maintenance reindex</h2>
  <p class="mt-2 text-sm text-base-content/60">
    Rebuild every index in the PostgreSQL database using REINDEX CONCURRENTLY. This is a long-running maintenance operation.
  </p>

  {#if databaseReindexError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{databaseReindexError}</span>
    </div>
  {/if}
  {#if databaseReindexMessage}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-success/30 bg-success/10 p-3 text-sm text-success" role="status">
      <CircleCheck class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{databaseReindexMessage}</span>
    </div>
  {/if}

  <div class="mt-4">
    <button class="btn btn-sm btn-neutral" disabled={databaseReindexBusy} onclick={reindexDatabase}>
      {#if databaseReindexBusy}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Database class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Reindex database
    </button>
  </div>
</section>

<!-- Delete media -->
<section class="rounded-2xl border border-error/30 bg-base-100 p-5 shadow-xl shadow-black/15 sm:p-6" aria-labelledby="metadata-delete-title">
  <h2 id="metadata-delete-title" class="text-base font-bold text-base-content">Delete media</h2>
  <p class="mt-2 text-sm text-base-content/60">
    Permanently delete a media item by its GUID, from every storage target or just one.
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
      <p class="mt-1.5 text-xs text-base-content/50">
        {#if deleteGuidValid && !storageKeysLoading && !storageKeysError && !deleteGuidExists}
          <span class="text-error">No media found for this GUID.</span>
        {:else if deleteGuid.trim() && !deleteGuidValid}
          <span class="text-error">Not a valid GUID.</span>
        {/if}
      </p>
    </div>
    <div>
      <label class="label mb-2 text-sm" for="delete-media-storage-key">Storage key</label>
      <Select
        items={storageKeyOptions}
        bind:value={deleteStorageKey}
        id="delete-media-storage-key"
        disabled={!canDelete}
      />
      <p class="mt-1.5 text-xs text-base-content/50">
        {#if storageKeysLoading}
          Loading storage keys for this GUID…
        {:else if storageKeysError}
          <span class="text-error">{storageKeysError}</span>
        {:else if canDelete}
          Leave "All storage targets" to remove every copy, its metadata, and search entries.
        {:else}
          Enter an existing media GUID to list its storage keys.
        {/if}
      </p>
    </div>
  </div>

  <div class="mt-4">
    <button class="btn btn-sm btn-error" disabled={deleteBusy || !canDelete} onclick={() => (deleteModalOpen = true)}>
      {#if deleteBusy}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Trash2 class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Delete media
    </button>
  </div>
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete media"
  message={deleteStorageKey.trim()
    ? `Delete the copy of media ${deleteGuid.trim()} stored on "${deleteStorageKey.trim()}"? If it is the last copy, the media's metadata and search entries are removed too.`
    : `Permanently delete media ${deleteGuid.trim()}? Every stored copy, its metadata, and search entries will be removed.`}
  confirmLabel="Delete media"
  onConfirm={removeMedia}
/>
