<script lang="ts">
  import { CircleAlert, CircleCheck, Repeat, Trash2 } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
  import { deleteMedia, deleteMediaForStorageKey, triggerReindex } from '$lib/api/metadata';

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';

  // Search reindex
  let reindexBusy = $state(false);
  let reindexMessage = $state<string | null>(null);
  let reindexError = $state<string | null>(null);

  // Delete media
  let deleteGuid = $state('');
  let deleteStorageKey = $state('');
  let deleteBusy = $state(false);
  let deleteError = $state<string | null>(null);
  let deleteMessage = $state<string | null>(null);
  let deleteModalOpen = $state(false);

  const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const deleteGuidValid = $derived(GUID_PATTERN.test(deleteGuid.trim()));

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
