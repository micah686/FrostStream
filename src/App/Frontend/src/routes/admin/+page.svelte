<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Spinner } from 'flowbite-svelte';
  import {
    ApiKeyOutline,
    ChartMixedOutline,
    ClockOutline,
    CloudArrowUpOutline,
    CubesStackedOutline,
    DatabaseOutline,
    ExclamationCircleOutline,
    EyeOutline,
    FileImportOutline,
    GlobeOutline,
    PlusOutline,
    TagOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import BackupsSection from '$lib/components/admin/BackupsSection.svelte';
  import BundleManagementSection from '$lib/components/admin/BundleManagementSection.svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import ImportsSection from '$lib/components/admin/ImportsSection.svelte';
  import MediaAccessSection from '$lib/components/admin/MediaAccessSection.svelte';
  import MetadataSection from '$lib/components/admin/MetadataSection.svelte';
  import SchedulesSection from '$lib/components/admin/SchedulesSection.svelte';
  import StatisticsSection from '$lib/components/admin/StatisticsSection.svelte';
  import {
    deleteStorage,
    listStorage,
    storageMethodLabel,
    storageSummary,
    type StorageConfig
  } from '$lib/api/storage';

  type IconComponent = typeof DatabaseOutline;

  interface AdminSection {
    label: string;
    icon: IconComponent;
  }

  const sections: AdminSection[] = [
    { label: 'Storage', icon: DatabaseOutline },
    { label: 'Statistics', icon: ChartMixedOutline },
    { label: 'Metadata', icon: TagOutline },
    { label: 'Import', icon: FileImportOutline },
    { label: 'Media access', icon: ApiKeyOutline },
    { label: 'Bundle management', icon: CubesStackedOutline },
    { label: 'Backups', icon: CloudArrowUpOutline },
    { label: 'Schedules', icon: ClockOutline }
  ];

  let activeSection = $state('Storage');

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
      return CloudArrowUpOutline;
    }
    if (storage.method === 'Network') {
      return GlobeOutline;
    }
    return DatabaseOutline;
  }
</script>

<svelte:head>
  <title>Administration · FrostStream</title>
</svelte:head>

<section class="min-h-[calc(100vh-7rem)]" aria-labelledby="admin-title">
  <div class="min-w-0">
    <h1 id="admin-title" class="text-2xl font-bold tracking-tight text-slate-100">Administration</h1>
    <p class="mt-2 text-sm text-slate-400">Server-wide settings · requires Owner</p>
  </div>

  <div class="mt-6 grid gap-6 xl:grid-cols-[16rem_minmax(0,1fr)]">
    <aside class="xl:pt-1" aria-label="Administration sections">
      <nav class="flex gap-2 overflow-x-auto pb-1 xl:block xl:space-y-2 xl:overflow-visible xl:pb-0">
        {#each sections as section}
          {@const { label, icon: Icon } = section}
          {@const active = activeSection === label}
          <button
            type="button"
            class={[
              'flex h-10 shrink-0 items-center gap-3 rounded-lg px-4 text-sm font-medium transition xl:w-full',
              active
                ? 'bg-blue-500/18 text-blue-400'
                : 'text-slate-400 hover:bg-slate-800/70 hover:text-slate-100'
            ]}
            aria-current={active ? 'page' : undefined}
            onclick={() => (activeSection = label)}
          >
            <Icon class="h-4.5 w-4.5 shrink-0" />
            <span class="whitespace-nowrap">{label}</span>
          </button>
        {/each}
      </nav>
    </aside>

    <div class="min-w-0 space-y-5">
      {#if activeSection === 'Storage'}
        <section
          class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6"
          aria-labelledby="storage-title"
        >
          <h2 id="storage-title" class="text-base font-bold text-slate-100">Storage targets</h2>
          <p class="mt-2 text-sm text-slate-400">
            Filesystems, network shares, and object stores FrostStream can index or write to.
          </p>

          {#if storageError}
            <div
              class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
              role="alert"
            >
              <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
              <span>{storageError}</span>
            </div>
          {/if}

          {#if storageLoading}
            <div class="mt-10 flex justify-center">
              <Spinner size="8" />
            </div>
          {:else if storageTargets.length === 0}
            <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
              <DatabaseOutline class="mx-auto h-9 w-9 text-slate-700" />
              <p class="mt-4 text-sm font-semibold text-slate-300">No storage targets yet</p>
              <p class="mt-1 text-sm text-slate-500">Register one so downloads have somewhere to land.</p>
            </div>
          {:else}
            <div class="mt-5 space-y-2">
              {#each storageTargets as storage (storage.key)}
                {@const Icon = storageIcon(storage)}
                <article
                  class="flex min-h-[3.95rem] flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:flex-row sm:items-center sm:px-4"
                >
                  <div class="flex min-w-0 items-center gap-3">
                    <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                      <Icon class="h-4.5 w-4.5" />
                    </span>
                    <div class="min-w-0">
                      <div class="flex min-w-0 flex-wrap items-center gap-2">
                        <h3 class="truncate text-sm font-semibold text-slate-100">{storage.key}</h3>
                        <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                          {storageMethodLabel(storage)}
                        </span>
                      </div>
                      <p class="mt-0.5 truncate font-mono text-xs text-slate-400" title={storageSummary(storage)}>
                        {storageSummary(storage)}
                      </p>
                      {#if storage.description}
                        <p class="mt-0.5 truncate text-xs text-slate-500">{storage.description}</p>
                      {/if}
                    </div>
                  </div>

                  <div class="flex shrink-0 gap-2 sm:ml-auto">
                    <a
                      href={`/admin/storage/${encodeURIComponent(storage.key)}`}
                      class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
                      aria-label={`View settings for storage target ${storage.key}`}
                    >
                      <EyeOutline class="h-4 w-4" />
                      Settings
                    </a>
                    {#if storage.key !== 'default'}
                      <button
                        type="button"
                        class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                        title="Delete storage target"
                        aria-label={`Delete storage target ${storage.key}`}
                        disabled={deletingKey === storage.key}
                        onclick={() => {
                          deleteTarget = storage;
                          deleteModalOpen = true;
                        }}
                      >
                        {#if deletingKey === storage.key}
                          <Spinner size="4" />
                        {:else}
                          <TrashBinOutline class="h-4 w-4" />
                        {/if}
                      </button>
                    {/if}
                  </div>
                </article>
              {/each}
            </div>
          {/if}

          <div class="mt-4">
            <Button
              href="/admin/storage/new"
              color="dark"
              class="border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
            >
              <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
              Register storage
            </Button>
          </div>
        </section>
      {:else if activeSection === 'Statistics'}
        <StatisticsSection />
      {:else if activeSection === 'Metadata'}
        <MetadataSection />
      {:else if activeSection === 'Import'}
        <ImportsSection />
      {:else if activeSection === 'Media access'}
        <MediaAccessSection />
      {:else if activeSection === 'Bundle management'}
        <BundleManagementSection />
      {:else if activeSection === 'Backups'}
        <BackupsSection />
      {:else if activeSection === 'Schedules'}
        <SchedulesSection />
      {:else}
        {@const Icon = sections.find((section) => section.label === activeSection)?.icon ?? DatabaseOutline}
        <section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
          <h2 class="text-base font-bold text-slate-100">{activeSection}</h2>
          <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
            <Icon class="mx-auto h-9 w-9 text-slate-700" />
            <p class="mt-4 text-sm font-semibold text-slate-300">Nothing here yet</p>
            <p class="mt-1 text-sm text-slate-500">{activeSection} settings are coming soon.</p>
          </div>
        </section>
      {/if}
    </div>
  </div>

  <ConfirmDeleteModal
    bind:open={deleteModalOpen}
    title="Delete storage target"
    message={deleteTarget ? `Delete storage target "${deleteTarget.key}"? Media already stored there will no longer be reachable through this key.` : ''}
    confirmLabel="Delete storage"
    onConfirm={async () => {
      if (deleteTarget) {
        await removeStorage(deleteTarget);
      }
    }}
  />
</section>
