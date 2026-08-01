<script lang="ts">
  import { onMount } from 'svelte';
  import {
    Archive,
    CircleAlert,
    Eye,
    Plus,
    SlidersHorizontal,
    Trash2
  } from '@lucide/svelte';
  import {
    deleteDownloadConfigSet,
    listDownloadConfigSets,
    type DownloadConfigSet
  } from '$lib/api/downloadConfigSets';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

  type IconComponent = typeof SlidersHorizontal;

  let configSets = $state<DownloadConfigSet[]>([]);
  let configSetsLoading = $state(true);
  let configSetsError = $state<string | null>(null);
  let deletingKey = $state<string | null>(null);
  let configPendingDelete = $state<DownloadConfigSet | null>(null);
  let configDeleteModalOpen = $state(false);

  onMount(() => {
    void loadConfigSets();
  });

  async function loadConfigSets() {
    configSetsLoading = true;
    configSetsError = null;
    try {
      configSets = await listDownloadConfigSets();
    } catch (err) {
      configSetsError = err instanceof Error ? err.message : 'Could not load config sets.';
    } finally {
      configSetsLoading = false;
    }
  }

  async function deleteConfigSet(config: DownloadConfigSet) {
    deletingKey = config.key;
    configSetsError = null;
    try {
      await deleteDownloadConfigSet(config.key);
      configSets = configSets.filter((item) => item.key !== config.key);
      configPendingDelete = null;
    } catch (err) {
      configSetsError = err instanceof Error ? err.message : 'Could not delete the config set.';
    } finally {
      deletingKey = null;
    }
  }

  function configSummary(config: DownloadConfigSet): string {
    return [
      config.storageKey ?? 'default storage',
      config.cookieProfileKey ? `cookie ${config.cookieProfileKey}` : null,
      `priority ${config.priority}`,
      config.ignoreKeywords.length > 0 ? `${config.ignoreKeywords.length} ignore ${config.ignoreKeywords.length === 1 ? 'keyword' : 'keywords'}` : null
    ].filter(Boolean).join(' · ');
  }

  function configIcon(config: DownloadConfigSet): IconComponent {
    return config.ytDlpOptions ? SlidersHorizontal : Archive;
  }
</script>

<section class="card border border-base-300 bg-base-100 p-5 sm:p-6">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 class="text-base font-bold text-base-content">Config sets</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Named presets for download and transcode options. Pick one when starting a new download, or set a default.
      </p>
    </div>
    <a class="btn btn-sm btn-neutral shrink-0" href="/profile/config-sets/new">
      <Plus class="mr-1.5 h-3.5 w-3.5" />
      New config set
    </a>
  </div>

  {#if configSetsError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{configSetsError}</span>
    </div>
  {/if}

  {#if configSetsLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if configSets.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <SlidersHorizontal class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No config sets yet</p>
      <p class="mt-1 text-sm text-base-content/50">Create one to reuse download and playlist settings.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each configSets as config (config.key)}
        {@const Icon = configIcon(config)}
        <article
          class="card flex min-h-[3.95rem] flex-col gap-3 border border-base-300 bg-base-100 p-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="flex min-w-0 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
              <Icon class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-base-content">{config.name}</h3>
                <span class="badge badge-sm badge-accent text-[10px] text-accent-content">
                  {config.key}
                </span>
              </div>
              <p class="mt-0.5 truncate text-xs text-base-content/60">
                {config.description || configSummary(config)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <a
              href={`/profile/config-sets/${encodeURIComponent(config.key)}`}
              class="btn btn-sm btn-neutral text-xs"
              aria-label={`Edit config set ${config.name}`}
            >
              <Eye class="mr-1.5 h-4 w-4" />
              Edit
            </a>
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Delete config set"
              aria-label={`Delete config set ${config.name}`}
              disabled={deletingKey === config.key}
              onclick={() => {
                configPendingDelete = config;
                configDeleteModalOpen = true;
              }}
            >
              {#if deletingKey === config.key}
                <span class="loading loading-spinner loading-xs mr-1.5"></span>
              {:else}
                <Trash2 class="mr-1.5 h-4 w-4" />
              {/if}
              Delete
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}

</section>

<ConfirmDeleteModal
  bind:open={configDeleteModalOpen}
  title="Delete config set"
  message={configPendingDelete ? `Delete config set "${configPendingDelete.name}"? This will not affect existing jobs.` : ''}
  confirmLabel="Delete config set"
  onConfirm={async () => {
    if (configPendingDelete) {
      await deleteConfigSet(configPendingDelete);
    }
  }}
/>
