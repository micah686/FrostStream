<script lang="ts">
  import { onMount } from 'svelte';
  import { Modal, Select } from '$lib/components/ui';
  import {
    Ban,
    CircleAlert,
    CirclePlus,
    Download,
    EyeOff,
    Image,
    Link,
    Pause,
    Pencil,
    Play,
    RefreshCw,
    Trash2,
    Users
  } from '@lucide/svelte';
  import {
    createCreatorSource,
    deleteCreatorSource,
    listCreatorSources,
    listIgnoredMedia,
    queueChannelDownload,
    refreshCreatorAssets,
    scanCreatorSource,
    updateCreatorSource,
    type CreatorScanMode,
    type CreatorSource,
    type CreatorSourceRequest,
    type IgnoredMedia
  } from '$lib/api/creatorSources';
  import { listDownloadConfigSets, type DownloadConfigSet } from '$lib/api/downloadConfigSets';
  import { formatRelativeDate } from '$lib/media';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

  const rowActionClass =
    'inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary disabled:opacity-50';

  interface SourceForm {
    sourceUrl: string;
    configSetKey: string;
    scanEnabled: boolean;
    incrementalPageSize: number;
    consecutiveKnownThreshold: number;
    fullRescanIntervalDays: number;
    updateCheckIntervalHours: number;
    metadataRefreshWindow: number;
  }

  function emptyForm(): SourceForm {
    return {
      sourceUrl: '',
      configSetKey: '',
      scanEnabled: true,
      incrementalPageSize: 50,
      consecutiveKnownThreshold: 25,
      fullRescanIntervalDays: 30,
      updateCheckIntervalHours: 6,
      metadataRefreshWindow: 25
    };
  }

  let sources = $state<CreatorSource[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let actionError = $state<string | null>(null);
  let actionNotice = $state<string | null>(null);
  let busy = $state<Record<number, string>>({});

  let formOpen = $state(false);
  let formBusy = $state(false);
  let formError = $state<string | null>(null);
  let form = $state<SourceForm>(emptyForm());
  let editingSource = $state<CreatorSource | null>(null);

  let deleteModalOpen = $state(false);
  let sourcePendingDelete = $state<CreatorSource | null>(null);

  let downloadModalOpen = $state(false);
  let sourcePendingDownload = $state<CreatorSource | null>(null);
  let downloadBusy = $state(false);
  let downloadError = $state<string | null>(null);
  let downloadStorageKey = $state('default');
  let downloadConfigSetKey = $state('');
  let downloadFetchComments = $state(false);
  let downloadForce = $state(false);
  let downloadOptionsLoaded = $state(false);
  let storageOptions = $state<Array<{ value: string; name: string }>>([
    { value: 'default', name: 'default' }
  ]);
  let configSets = $state<DownloadConfigSet[]>([]);

  const configSetOptions = $derived([
    { value: '', name: 'None (use per-request settings)' },
    ...configSets.map((set) => ({ value: set.key, name: set.name }))
  ]);
  const downloadConfigSetSelected = $derived(downloadConfigSetKey !== '');

  let expandedIgnoredId = $state<number | null>(null);
  let ignoredItems = $state<IgnoredMedia[]>([]);
  let ignoredLoading = $state(false);
  let ignoredError = $state<string | null>(null);

  const trackedCount = $derived(sources.length);
  const scanningCount = $derived(sources.filter((source) => source.scanEnabled).length);
  const scannedCount = $derived(sources.filter((source) => source.lastSuccessfulScanAt).length);

  onMount(() => {
    void loadSources();
    void loadConfigSets();
  });

  async function loadSources() {
    loading = true;
    loadError = null;
    try {
      sources = sortSources(await listCreatorSources());
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load creator sources.';
    } finally {
      loading = false;
    }
  }

  function sortSources(items: CreatorSource[]): CreatorSource[] {
    return [...items].sort((a, b) => displayName(a).localeCompare(displayName(b)) || a.id - b.id);
  }

  function openCreateForm() {
    editingSource = null;
    form = emptyForm();
    formError = null;
    formOpen = true;
  }

  function openEditForm(source: CreatorSource) {
    editingSource = source;
    form = {
      sourceUrl: source.sourceUrl,
      configSetKey: source.configSetKey ?? '',
      scanEnabled: source.scanEnabled,
      incrementalPageSize: source.incrementalPageSize,
      consecutiveKnownThreshold: source.consecutiveKnownThreshold,
      fullRescanIntervalDays: source.fullRescanIntervalDays,
      updateCheckIntervalHours: source.updateCheckIntervalHours,
      metadataRefreshWindow: source.metadataRefreshWindow
    };
    formError = null;
    formOpen = true;
  }

  function buildRequest(): CreatorSourceRequest {
    return {
      sourceUrl: form.sourceUrl.trim(),
      configSetKey: form.configSetKey || null,
      scanEnabled: form.scanEnabled,
      incrementalPageSize: Number(form.incrementalPageSize),
      consecutiveKnownThreshold: Number(form.consecutiveKnownThreshold),
      fullRescanIntervalDays: Number(form.fullRescanIntervalDays),
      updateCheckIntervalHours: Number(form.updateCheckIntervalHours),
      metadataRefreshWindow: Number(form.metadataRefreshWindow)
    };
  }

  function validateForm(): string | null {
    if (!form.sourceUrl.trim()) {
      return 'Source URL is required.';
    }
    const ranges: Array<[string, number, number, number]> = [
      ['Incremental page size', Number(form.incrementalPageSize), 1, 500],
      ['Consecutive known threshold', Number(form.consecutiveKnownThreshold), 1, 500],
      ['Full rescan interval', Number(form.fullRescanIntervalDays), 1, 365],
      ['Update check interval', Number(form.updateCheckIntervalHours), 1, 168],
      ['Metadata refresh window', Number(form.metadataRefreshWindow), 1, 500]
    ];
    for (const [label, value, min, max] of ranges) {
      if (!Number.isInteger(value) || value < min || value > max) {
        return `${label} must be a whole number from ${min} to ${max}.`;
      }
    }
    return null;
  }

  async function submitForm(event: SubmitEvent) {
    event.preventDefault();
    const validationError = validateForm();
    if (validationError) {
      formError = validationError;
      return;
    }

    formBusy = true;
    formError = null;
    try {
      if (editingSource) {
        const updated = await updateCreatorSource(editingSource.id, buildRequest());
        sources = sortSources(sources.map((item) => (item.id === updated.id ? updated : item)));
      } else {
        const created = await createCreatorSource(buildRequest());
        sources = sortSources([...sources, created]);
      }
      formOpen = false;
    } catch (err) {
      formError = err instanceof Error ? err.message : 'Could not save the creator source.';
    } finally {
      formBusy = false;
    }
  }

  async function toggleScanning(source: CreatorSource) {
    await runAction(source.id, 'scan', async () => {
      const updated = await updateCreatorSource(source.id, {
        sourceUrl: source.sourceUrl,
        configSetKey: source.configSetKey,
        scanEnabled: !source.scanEnabled,
        incrementalPageSize: source.incrementalPageSize,
        consecutiveKnownThreshold: source.consecutiveKnownThreshold,
        fullRescanIntervalDays: source.fullRescanIntervalDays,
        updateCheckIntervalHours: source.updateCheckIntervalHours,
        metadataRefreshWindow: source.metadataRefreshWindow
      });
      sources = sortSources(sources.map((item) => (item.id === updated.id ? updated : item)));
    });
  }

  async function refreshAssets(source: CreatorSource, force: boolean) {
    await runAction(source.id, 'assets', async () => {
      await refreshCreatorAssets(source.id, force);
      actionNotice = `Asset refresh queued for ${displayName(source)}${force ? ' (forced)' : ''}.`;
    });
  }

  async function scanNow(source: CreatorSource, mode: CreatorScanMode) {
    await runAction(source.id, 'scan-now', async () => {
      await scanCreatorSource(source.id, mode);
      actionNotice = `${mode === 'full' ? 'Full' : 'Incremental'} scan queued for ${displayName(source)}.`;
    });
  }

  function openDownloadModal(source: CreatorSource) {
    sourcePendingDownload = source;
    downloadError = null;
    downloadForce = false;
    downloadModalOpen = true;
    if (!downloadOptionsLoaded) {
      downloadOptionsLoaded = true;
      void loadDownloadOptions();
    }
  }

  async function loadDownloadOptions() {
    try {
      const response = await fetch('/api/storage/list');
      if (response.ok) {
        const items = (await response.json()) as { key: string; description?: string | null }[];
        if (items.length > 0) {
          storageOptions = items.map((item) => ({
            value: item.key,
            name: item.description ? `${item.key} — ${item.description}` : item.key
          }));
          if (!storageOptions.some((option) => option.value === downloadStorageKey)) {
            downloadStorageKey = storageOptions[0].value;
          }
        }
      }
    } catch {
      // Keep the default storage key when the list is unavailable.
    }
    await loadConfigSets();
  }

  async function loadConfigSets() {
    try {
      configSets = await listDownloadConfigSets();
    } catch {
      // Config sets are optional; creator downloads work without them.
    }
  }

  async function submitChannelDownload(event: SubmitEvent) {
    event.preventDefault();
    const source = sourcePendingDownload;
    if (!source) {
      return;
    }

    downloadBusy = true;
    downloadError = null;
    try {
      const result = await queueChannelDownload(
        downloadConfigSetSelected
          ? {
              sourceUrl: source.sourceUrl,
              storageKey: null,
              configSetKey: downloadConfigSetKey,
              fetchComments: false,
              forceDownload: downloadForce
            }
          : {
              sourceUrl: source.sourceUrl,
              storageKey: downloadStorageKey.trim() || 'default',
              configSetKey: null,
              fetchComments: downloadFetchComments,
              forceDownload: downloadForce
            }
      );
      actionNotice = `Full channel download queued for ${displayName(source)} (group ${result.correlationId}).`;
      actionError = null;
      downloadModalOpen = false;
    } catch (err) {
      downloadError = err instanceof Error ? err.message : 'Could not queue the channel download.';
    } finally {
      downloadBusy = false;
    }
  }

  function requestDelete(source: CreatorSource) {
    sourcePendingDelete = source;
    deleteModalOpen = true;
  }

  async function confirmDelete() {
    const source = sourcePendingDelete;
    if (!source) {
      return;
    }
    await deleteCreatorSource(source.id);
    sources = sources.filter((item) => item.id !== source.id);
    if (expandedIgnoredId === source.id) {
      expandedIgnoredId = null;
    }
  }

  async function toggleIgnored(source: CreatorSource) {
    if (expandedIgnoredId === source.id) {
      expandedIgnoredId = null;
      return;
    }
    expandedIgnoredId = source.id;
    ignoredItems = [];
    ignoredError = null;
    ignoredLoading = true;
    try {
      const items = await listIgnoredMedia(source.id);
      if (expandedIgnoredId === source.id) {
        ignoredItems = items;
      }
    } catch (err) {
      if (expandedIgnoredId === source.id) {
        ignoredError = err instanceof Error ? err.message : 'Could not load ignored videos.';
      }
    } finally {
      if (expandedIgnoredId === source.id) {
        ignoredLoading = false;
      }
    }
  }

  async function runAction(sourceId: number, action: string, fn: () => Promise<void>) {
    actionError = null;
    actionNotice = null;
    busy = { ...busy, [sourceId]: action };
    try {
      await fn();
    } catch (err) {
      actionError = err instanceof Error ? err.message : `Could not ${action} the creator source.`;
    } finally {
      const { [sourceId]: _removed, ...rest } = busy;
      busy = rest;
    }
  }

  function displayName(source: CreatorSource): string {
    try {
      const url = new URL(source.sourceUrl);
      const segments = url.pathname.split('/').filter(Boolean);
      const handle = segments.find((segment) => segment.startsWith('@')) ?? segments.at(-1);
      return handle ? decodeURIComponent(handle) : url.hostname.replace(/^www\./, '');
    } catch {
      return source.sourceUrl;
    }
  }

  function compactUrl(sourceUrl: string): string {
    try {
      const url = new URL(sourceUrl);
      return `${url.hostname.replace(/^www\./, '')}${url.pathname === '/' ? '' : url.pathname}`;
    } catch {
      return sourceUrl;
    }
  }

  function formatScan(iso: string | null): string {
    return formatRelativeDate(iso) ?? 'never';
  }
</script>

<svelte:head>
  <title>Creators · FrostStream</title>
</svelte:head>

<section aria-labelledby="creators-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="creators-title" class="text-2xl font-bold tracking-tight text-base-content">Creators</h1>
      <p class="mt-1 text-sm text-base-content/50">
        Channels tracked for automatic discovery and download of new uploads
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <button class="btn btn-sm btn-neutral text-xs" onclick={loadSources} disabled={loading}>
        {#if loading}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <RefreshCw class="mr-1.5 h-4 w-4" />
        {/if}
        Refresh
      </button>
      <button class="btn btn-sm btn-primary text-xs" onclick={openCreateForm}>
        <CirclePlus class="mr-1.5 h-4 w-4" />
        Track creator
      </button>
    </div>
  </div>

  <div class="mt-6 grid gap-3 md:grid-cols-3">
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Tracked</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{trackedCount}</p>
      <p class="mt-1 text-xs text-base-content/50">creator sources</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Scanning</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{scanningCount}</p>
      <p class="mt-1 text-xs text-base-content/50">enabled for discovery</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Scanned</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{scannedCount}</p>
      <p class="mt-1 text-xs text-base-content/50">completed at least one scan</p>
    </div>
  </div>

  {#if loadError || actionError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionError ?? loadError}</span>
    </div>
  {/if}

  {#if actionNotice}
    <div
      class="mt-5 flex items-start gap-3 rounded-xl border border-success/30 bg-success/10 p-4 text-sm text-success"
      role="status"
    >
      <RefreshCw class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionNotice}</span>
    </div>
  {/if}

  {#if loading && sources.length === 0}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if sources.length === 0}
    <div class="mt-8 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <Users class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No creators tracked yet</p>
      <p class="mt-1 text-sm text-base-content/50">
        Track a channel to automatically discover and download everything it uploads.
      </p>
      <button class="btn btn-sm btn-primary mt-5 text-xs" onclick={openCreateForm}>
        <CirclePlus class="mr-1.5 h-4 w-4" />
        Track your first creator
      </button>
    </div>
  {:else}
    <ul class="mt-6 space-y-3">
      {#each sources as source (source.id)}
        {@const busyAction = busy[source.id]}
        <li
          class={[
            'rounded-2xl border p-5',
            source.scanEnabled ? 'border-base-300/90 bg-base-200/45' : 'border-base-300/60 bg-base-200/25'
          ]}
        >
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="truncate text-base font-semibold text-base-content">{displayName(source)}</h2>
                <span
                  class={[
                    'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1',
                    source.scanEnabled
                      ? 'bg-success/12 text-success ring-success/20'
                      : 'bg-base-200/12 text-base-content/60 ring-base-300/20'
                  ]}
                >
                  {source.scanEnabled ? 'SCANNING' : 'PAUSED'}
                </span>
              </div>
              <a
                href={source.sourceUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="mt-1.5 inline-flex max-w-full items-center gap-1.5 text-xs text-base-content/50 transition hover:text-primary"
              >
                <Link class="h-3.5 w-3.5 shrink-0" />
                <span class="truncate">{compactUrl(source.sourceUrl)}</span>
              </a>
            </div>

            <div class="flex flex-wrap items-center gap-2">
              <button
                type="button"
                class={rowActionClass}
                disabled={Boolean(busyAction)}
                onclick={() => toggleScanning(source)}
                title={source.scanEnabled ? 'Pause automatic scanning' : 'Resume automatic scanning'}
              >
                {#if busyAction === 'scan'}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else if source.scanEnabled}
                  <Pause class="h-4 w-4" />
                {:else}
                  <Play class="h-4 w-4" />
                {/if}
                {source.scanEnabled ? 'Pause' : 'Resume'}
              </button>
              <button
                type="button"
                class={rowActionClass}
                disabled={Boolean(busyAction)}
                onclick={(event) => refreshAssets(source, event.shiftKey)}
                title="Queue a refresh of the avatar and banner (hold Shift to force re-download)"
              >
                {#if busyAction === 'assets'}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <Image class="h-4 w-4" />
                {/if}
                Refresh assets
              </button>
              <button
                type="button"
                class={rowActionClass}
                disabled={Boolean(busyAction)}
                onclick={(event) => scanNow(source, event.shiftKey ? 'full' : 'incremental')}
                title="Queue an immediate scan for new uploads (hold Shift for a full channel rescan)"
              >
                {#if busyAction === 'scan-now'}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <RefreshCw class="h-4 w-4" />
                {/if}
                Scan now
              </button>
              <button
                type="button"
                class={rowActionClass}
                disabled={Boolean(busyAction)}
                onclick={() => openDownloadModal(source)}
                title="Queue a one-off download of the channel's full backlog"
              >
                <Download class="h-4 w-4" />
                Download channel
              </button>
              <button
                type="button"
                class={rowActionClass}
                disabled={Boolean(busyAction)}
                onclick={() => openEditForm(source)}
              >
                <Pencil class="h-4 w-4" />
                Edit
              </button>
              <button
                type="button"
                class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-error/60 hover:bg-error/10 hover:text-error disabled:opacity-50"
                disabled={Boolean(busyAction)}
                onclick={() => requestDelete(source)}
              >
                <Trash2 class="h-4 w-4" />
                Delete
              </button>
            </div>
          </div>

          <dl class="mt-4 grid gap-x-6 gap-y-2 text-xs sm:grid-cols-2 lg:grid-cols-4">
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Last scan</dt>
              <dd class="mt-0.5 text-base-content/80">{formatScan(source.lastSuccessfulScanAt)}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Last full scan</dt>
              <dd class="mt-0.5 text-base-content/80">{formatScan(source.lastFullScanAt)}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Update check</dt>
              <dd class="mt-0.5 text-base-content/80">every {source.updateCheckIntervalHours} h</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Full rescan</dt>
              <dd class="mt-0.5 text-base-content/80">every {source.fullRescanIntervalDays} days</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Scan paging</dt>
              <dd class="mt-0.5 text-base-content/80">
                {source.incrementalPageSize} per page · stop after {source.consecutiveKnownThreshold} known
              </dd>
            </div>
          </dl>

          <div class="mt-4 border-t border-base-300/70 pt-3">
            <button
              type="button"
              class="inline-flex items-center gap-1.5 text-xs font-semibold text-base-content/50 transition hover:text-primary"
              onclick={() => toggleIgnored(source)}
            >
              <EyeOff class="h-3.5 w-3.5" />
              {expandedIgnoredId === source.id ? 'Hide ignored videos' : 'Show ignored videos'}
            </button>

            {#if expandedIgnoredId === source.id}
              <div class="mt-3">
                {#if ignoredLoading}
                  <div class="flex justify-center py-6">
                    <span class="loading loading-spinner loading-sm"></span>
                  </div>
                {:else if ignoredError}
                  <p class="text-xs text-error">{ignoredError}</p>
                {:else if ignoredItems.length === 0}
                  <p class="text-xs text-base-content/50">
                    No videos have been suppressed by ignore keywords for this creator.
                  </p>
                {:else}
                  <ul class="space-y-2">
                    {#each ignoredItems as item (item.id)}
                      <li
                        class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-base-300/70 bg-base-200/40 px-3 py-2"
                      >
                        <div class="min-w-0">
                          <a
                            href={item.canonicalUrl}
                            target="_blank"
                            rel="noopener noreferrer"
                            class="block truncate text-xs font-medium text-base-content/80 transition hover:text-primary"
                          >
                            {item.title?.trim() || item.canonicalUrl}
                          </a>
                          <p class="mt-0.5 text-[11px] text-base-content/40">
                            {[
                              formatRelativeDate(item.firstSeenAt)
                                ? `first seen ${formatRelativeDate(item.firstSeenAt)}`
                                : null,
                              formatRelativeDate(item.lastSeenAt)
                                ? `last seen ${formatRelativeDate(item.lastSeenAt)}`
                                : null
                            ]
                              .filter(Boolean)
                              .join(' · ')}
                          </p>
                        </div>
                        {#if item.ignoredKeyword}
                          <span
                            class="inline-flex items-center gap-1 rounded-full bg-warning/10 px-2 py-0.5 text-[10px] font-bold text-warning ring-1 ring-warning/20"
                          >
                            <Ban class="h-3 w-3" />
                            {item.ignoredKeyword}
                          </span>
                        {/if}
                      </li>
                    {/each}
                  </ul>
                {/if}
              </div>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<Modal bind:open={formOpen} title={editingSource ? 'Edit creator source' : 'Track a creator'} size="lg">
  <form id="creator-source-form" class="space-y-4" onsubmit={submitForm}>
    <div>
      <label class="label mb-1.5 text-xs" for="creator-source-url">
        Channel URL
      </label>
      <input class="input w-full" id="creator-source-url" type="url" required
         bind:value={form.sourceUrl} placeholder="https://www.youtube.com/@creator/videos" />
      <p class="mt-1.5 text-[11px] text-base-content/40">
        The channel or listing page that discovery scans will page through for new uploads.
      </p>
    </div>

    <div>
      <label class="label mb-1.5 text-xs" for="creator-config-set">Config set</label>
      <Select id="creator-config-set" items={configSetOptions} bind:value={form.configSetKey} />
      <p class="mt-1.5 text-[11px] text-base-content/40">
        Applies this set's storage target, cookie profile, yt-dlp options, priority, and worker tag to future downloads from this creator.
      </p>
    </div>

    <section class="rounded-xl border border-base-300/80 bg-base-200/40 p-4" aria-labelledby="creator-scan-schedule-title">
      <h3 id="creator-scan-schedule-title" class="text-sm font-semibold text-base-content/90">
        How scheduled scans work
      </h3>
      <p class="mt-1.5 text-xs leading-5 text-base-content/60">
        The <a class="link link-hover text-primary" href="/admin/schedules">Schedules</a> page controls global sweep timers. A sweep wakes discovery, then only enabled creators whose own interval has elapsed participate.
      </p>
      <dl class="mt-3 space-y-3 border-t border-base-300/70 pt-3 text-xs leading-5">
        <div>
          <dt class="font-semibold text-base-content/90">channel-scan-refresh</dt>
          <dd class="mt-0.5 text-base-content/60">
            Checks creators due for an update, then scans only the newest entries using this creator's incremental page size.
          </dd>
        </div>
        <div>
          <dt class="font-semibold text-base-content/90">channel-full-rescan</dt>
          <dd class="mt-0.5 text-base-content/60">
            Selects creators due for a full rescan, then walks each listing in page-sized chunks until the full pass completes.
          </dd>
        </div>
      </dl>
      <p class="mt-3 text-[11px] leading-4 text-base-content/50">
        The seeded schedules run refresh sweeps every 30 minutes and full sweeps daily, but their configured cadence is authoritative. The fields below decide when this creator becomes due; it runs on the next applicable sweep.
      </p>
    </section>

    <div class="grid gap-4 sm:grid-cols-2">
      <div>
        <label class="label mb-1.5 text-xs" for="creator-page-size">
          Incremental page size
        </label>
        <input class="input w-full" id="creator-page-size" type="number" min="1" max="500" required
           bind:value={form.incrementalPageSize} />
        <p class="mt-1.5 text-[11px] text-base-content/40">Videos fetched per page during a scan (1-500).</p>
      </div>
      <div>
        <label class="label mb-1.5 text-xs" for="creator-known-threshold">
          Consecutive known threshold
        </label>
        <input class="input w-full" id="creator-known-threshold" type="number" min="1" max="500" required
           bind:value={form.consecutiveKnownThreshold} />
        <p class="mt-1.5 text-[11px] text-base-content/40">
          Stop scanning after this many already-known videos in a row (1-500).
        </p>
      </div>
      <div>
        <label class="label mb-1.5 text-xs" for="creator-rescan-days">
          Full rescan interval (days)
        </label>
        <input class="input w-full" id="creator-rescan-days" type="number" min="1" max="365" required
           bind:value={form.fullRescanIntervalDays} />
        <p class="mt-1.5 text-[11px] text-base-content/40">How often the entire channel is re-walked (1-365).</p>
      </div>
      <div>
        <label class="label mb-1.5 text-xs" for="creator-update-check-hours">
          Update check interval (hours)
        </label>
        <input class="input w-full" id="creator-update-check-hours" type="number" min="1" max="168" required
           bind:value={form.updateCheckIntervalHours} />
        <p class="mt-1.5 text-[11px] text-base-content/40">
          Minimum hours between checks for new uploads (1-168).
        </p>
      </div>
      <div>
        <label class="label mb-1.5 text-xs" for="creator-metadata-window">
          Metadata refresh window
        </label>
        <input class="input w-full" id="creator-metadata-window" type="number" min="1" max="500" required
           bind:value={form.metadataRefreshWindow} />
        <p class="mt-1.5 text-[11px] text-base-content/40">
          Recent videos whose metadata is refreshed on each scan (1-500).
        </p>
      </div>
    </div>

    <div class="flex items-center justify-between rounded-xl border border-base-300/80 bg-base-200/40 px-4 py-3">
      <div>
        <p class="text-sm font-semibold text-base-content/90">Automatic scanning</p>
        <p class="mt-0.5 text-xs text-base-content/50">
          Discover and download new uploads from this creator on the recurring schedule.
        </p>
      </div>
      <input type="checkbox" class="toggle" bind:checked={form.scanEnabled} />
    </div>

    {#if formError}
      <div class="alert alert-error text-sm" role="alert">
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{formError}</span>
      </div>
    {/if}
  </form>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" disabled={formBusy} onclick={() => (formOpen = false)}>
        Cancel
      </button>
      <button class="btn btn-sm btn-primary text-xs" type="submit" form="creator-source-form" disabled={formBusy}>
        {#if formBusy}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {/if}
        {editingSource ? 'Save changes' : 'Track creator'}
      </button>
    </div>
  {/snippet}
</Modal>

<Modal bind:open={downloadModalOpen} title="Queue full channel download" size="md">
  <form id="channel-download-form" class="space-y-4" onsubmit={submitChannelDownload}>
    <p class="text-sm text-base-content/60">
      Queue a full scan and download of
      <span class="font-semibold text-base-content/90">
        {sourcePendingDownload ? displayName(sourcePendingDownload) : 'this channel'}
      </span>. Videos already in the library are skipped, and config-set ignore keywords are applied.
    </p>

    <div>
      <label class="label mb-1.5 text-xs" for="channel-download-config-set">
        Config set
      </label>
      <Select id="channel-download-config-set" items={configSetOptions} bind:value={downloadConfigSetKey} />
      <p class="mt-1.5 text-[11px] text-base-content/40">
        Applies its saved storage target, yt-dlp options, ignore keywords, priority, and worker tag to every discovered video. Other options below except force re-download are disabled while a config set is selected.
      </p>
    </div>

    <div>
      <label class="label mb-1.5 text-xs" for="channel-download-storage">
        Storage target
      </label>
      <Select id="channel-download-storage" items={storageOptions} bind:value={downloadStorageKey} disabled={downloadConfigSetSelected} />
    </div>

    <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={downloadFetchComments} disabled={downloadConfigSetSelected} /><span>Fetch comments</span></label>

    <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={downloadForce} /><span>Force re-download videos already in the library</span></label>

    {#if downloadError}
      <div
        class="alert alert-error text-sm"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{downloadError}</span>
      </div>
    {/if}
  </form>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" disabled={downloadBusy} onclick={() => (downloadModalOpen = false)}>
        Cancel
      </button>
      <button class="btn btn-sm btn-primary text-xs" type="submit" form="channel-download-form" disabled={downloadBusy}>
        {#if downloadBusy}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <Download class="mr-1.5 h-4 w-4" />
        {/if}
        Queue download
      </button>
    </div>
  {/snippet}
</Modal>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Stop tracking creator"
  message={`Stop tracking ${sourcePendingDelete ? displayName(sourcePendingDelete) : 'this creator'}? Future discovery scans will no longer run for this source. Already downloaded videos are kept.`}
  confirmLabel="Stop tracking"
  onConfirm={confirmDelete}
/>
