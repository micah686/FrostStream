<script lang="ts">
  import { onMount } from 'svelte';
  import {
    CircleAlert,
    CloudUpload,
    Database,
    Eye,
    Globe,
    Plus,
    Trash2
  } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    deleteStorage,
    listStorage,
    storageMethodLabel,
    storageSummary,
    type StorageConfig
  } from '$lib/api/storage';

  type IconComponent = typeof Database;

  let storageTargets = $state<StorageConfig[]>([]);
  let storageLoading = $state(true);
  let storageError = $state<string | null>(null);
  let deletingKey = $state<string | null>(null);
  let deleteTarget = $state<StorageConfig | null>(null);
  let deleteModalOpen = $state(false);

  onMount(() => {
    void loadStorage();
  });

  async function loadStorage() {
    storageLoading = true;
    storageError = null;
    try {
      storageTargets = await listStorage();
    } catch (err) {
      storageError = err instanceof Error ? err.message : 'Could not load storage targets.';
    } finally {
      storageLoading = false;
    }
  }

  async function removeStorage(storage: StorageConfig) {
    try {
      deletingKey = storage.key;
      storageError = null;
      await deleteStorage(storage.key);
      storageTargets = storageTargets.filter((item) => item.key !== storage.key);
      deleteTarget = null;
      deleteModalOpen = false;
    } catch (err) {
      storageError = err instanceof Error ? err.message : 'Could not delete the storage target.';
    } finally {
      deletingKey = null;
    }
  }

  function storageIcon(storage: StorageConfig): IconComponent {
    if (storage.method === 'ObjectStorage') {
      return CloudUpload;
    }
    if (storage.method === 'Network') {
      return Globe;
    }
    return Database;
  }
</script>

<section
  class="card border border-base-300 bg-base-100 p-5 sm:p-6"
  aria-labelledby="storage-title"
>
  <h2 id="storage-title" class="text-base font-bold text-base-content">Storage targets</h2>
  <p class="mt-2 text-sm text-base-content/60">
    Filesystems, network shares, and object stores FrostStream can index or write to.
  </p>

  {#if storageError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{storageError}</span>
    </div>
  {/if}

  {#if storageLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if storageTargets.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <Database class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No storage targets yet</p>
      <p class="mt-1 text-sm text-base-content/50">Register one so downloads have somewhere to land.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each storageTargets as storage (storage.key)}
        {@const Icon = storageIcon(storage)}
        <article
          class="flex min-h-[3.95rem] flex-col gap-3 rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="flex min-w-0 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
              <Icon class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-base-content">{storage.key}</h3>
                <span class="rounded-full bg-base-300 px-2 py-0.5 text-[10px] font-semibold text-base-content/60">
                  {storageMethodLabel(storage)}
                </span>
              </div>
              <p class="mt-0.5 truncate font-mono text-xs text-base-content/60" title={storageSummary(storage)}>
                {storageSummary(storage)}
              </p>
              {#if storage.description}
                <p class="mt-0.5 truncate text-xs text-base-content/50">{storage.description}</p>
              {/if}
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <a
              href={`/admin/storage/${encodeURIComponent(storage.key)}`}
              class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary"
              aria-label={`View settings for storage target ${storage.key}`}
            >
              <Eye class="h-4 w-4" />
              Settings
            </a>
            {#if storage.key !== 'default'}
              <button
                type="button"
                class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-base-content/80 transition hover:border-error/60 hover:bg-error/10 hover:text-error disabled:opacity-50"
                title="Delete storage target"
                aria-label={`Delete storage target ${storage.key}`}
                disabled={deletingKey === storage.key}
                onclick={() => {
                  deleteTarget = storage;
                  deleteModalOpen = true;
                }}
              >
                {#if deletingKey === storage.key}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <Trash2 class="h-4 w-4" />
                {/if}
              </button>
            {/if}
          </div>
        </article>
      {/each}
    </div>
  {/if}

  <div class="mt-4">
    <a class="btn btn-sm btn-ghost text-xs" href="/admin/storage/new">
      <Plus class="mr-1.5 h-3.5 w-3.5" />
      Register storage
    </a>
  </div>
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete storage target"
  message={deleteTarget
    ? `Delete storage target "${deleteTarget.key}"? Media already stored there will no longer be reachable through this key.`
    : ''}
  confirmLabel="Delete storage"
  onConfirm={async () => {
    if (deleteTarget) {
      await removeStorage(deleteTarget);
    }
  }}
/>
