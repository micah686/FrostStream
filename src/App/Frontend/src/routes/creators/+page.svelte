<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Input, Label, Modal, Select, Spinner, Toggle } from 'flowbite-svelte';
  import {
    BanOutline,
    CirclePlusOutline,
    EditOutline,
    ExclamationCircleOutline,
    EyeSlashOutline,
    ImageOutline,
    LinkOutline,
    PauseOutline,
    PlayOutline,
    RefreshOutline,
    TrashBinOutline,
    UsersGroupOutline
  } from 'flowbite-svelte-icons';
  import {
    createCreatorSource,
    creatorSourceTypes,
    deleteCreatorSource,
    listCreatorSources,
    listIgnoredMedia,
    refreshCreatorAssets,
    updateCreatorSource,
    type CreatorSource,
    type CreatorSourceRequest,
    type CreatorSourceType,
    type IgnoredMedia
  } from '$lib/api/creatorSources';
  import { formatRelativeDate } from '$lib/media';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

  const rowActionClass =
    'inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-50';
  const fieldClass =
    'border-slate-700! bg-slate-900/80! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';

  interface SourceForm {
    platform: string;
    sourceType: CreatorSourceType;
    sourceUrl: string;
    scanEnabled: boolean;
    incrementalPageSize: number;
    consecutiveKnownThreshold: number;
    fullRescanIntervalDays: number;
    metadataRefreshWindow: number;
  }

  const sourceTypeOptions = creatorSourceTypes.map((type) => ({ value: type, name: type }));

  function emptyForm(): SourceForm {
    return {
      platform: 'youtube',
      sourceType: 'Videos',
      sourceUrl: '',
      scanEnabled: true,
      incrementalPageSize: 50,
      consecutiveKnownThreshold: 25,
      fullRescanIntervalDays: 30,
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

  let expandedIgnoredId = $state<number | null>(null);
  let ignoredItems = $state<IgnoredMedia[]>([]);
  let ignoredLoading = $state(false);
  let ignoredError = $state<string | null>(null);

  const trackedCount = $derived(sources.length);
  const scanningCount = $derived(sources.filter((source) => source.scanEnabled).length);
  const platformCount = $derived(new Set(sources.map((source) => source.platform.toLowerCase())).size);
  const scannedCount = $derived(sources.filter((source) => source.lastSuccessfulScanAt).length);

  onMount(() => {
    void loadSources();
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
    return [...items].sort(
      (a, b) =>
        a.platform.localeCompare(b.platform) ||
        displayName(a).localeCompare(displayName(b)) ||
        a.id - b.id
    );
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
      platform: source.platform,
      sourceType: source.sourceType,
      sourceUrl: source.sourceUrl,
      scanEnabled: source.scanEnabled,
      incrementalPageSize: source.incrementalPageSize,
      consecutiveKnownThreshold: source.consecutiveKnownThreshold,
      fullRescanIntervalDays: source.fullRescanIntervalDays,
      metadataRefreshWindow: source.metadataRefreshWindow
    };
    formError = null;
    formOpen = true;
  }

  function buildRequest(source: CreatorSource | null): CreatorSourceRequest {
    return {
      platform: form.platform.trim(),
      sourceType: form.sourceType,
      sourceUrl: form.sourceUrl.trim(),
      scanEnabled: form.scanEnabled,
      incrementalPageSize: Number(form.incrementalPageSize),
      consecutiveKnownThreshold: Number(form.consecutiveKnownThreshold),
      fullRescanIntervalDays: Number(form.fullRescanIntervalDays),
      metadataRefreshWindow: Number(form.metadataRefreshWindow),
      providerQueryLimits: source?.providerQueryLimits ?? null
    };
  }

  function validateForm(): string | null {
    if (!form.platform.trim()) {
      return 'Platform is required.';
    }
    if (!form.sourceUrl.trim()) {
      return 'Source URL is required.';
    }
    const ranges: Array<[string, number, number, number]> = [
      ['Incremental page size', Number(form.incrementalPageSize), 1, 500],
      ['Consecutive known threshold', Number(form.consecutiveKnownThreshold), 1, 500],
      ['Full rescan interval', Number(form.fullRescanIntervalDays), 1, 365],
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
        const updated = await updateCreatorSource(editingSource.id, buildRequest(editingSource));
        sources = sortSources(sources.map((item) => (item.id === updated.id ? updated : item)));
      } else {
        const created = await createCreatorSource(buildRequest(null));
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
        platform: source.platform,
        sourceType: source.sourceType,
        sourceUrl: source.sourceUrl,
        scanEnabled: !source.scanEnabled,
        incrementalPageSize: source.incrementalPageSize,
        consecutiveKnownThreshold: source.consecutiveKnownThreshold,
        fullRescanIntervalDays: source.fullRescanIntervalDays,
        metadataRefreshWindow: source.metadataRefreshWindow,
        providerQueryLimits: source.providerQueryLimits
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
      <h1 id="creators-title" class="text-2xl font-bold tracking-tight text-white">Creators</h1>
      <p class="mt-1 text-sm text-slate-500">
        Channels tracked for automatic discovery and download of new uploads
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <Button
        color="dark"
        onclick={loadSources}
        disabled={loading}
        class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
      >
        {#if loading}
          <Spinner size="4" class="mr-1.5" />
        {:else}
          <RefreshOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Refresh
      </Button>
      <Button
        color="blue"
        onclick={openCreateForm}
        class="border-0! bg-blue-500! px-3! py-2! text-xs! font-semibold! hover:bg-blue-400!"
      >
        <CirclePlusOutline class="mr-1.5 h-4 w-4" />
        Track creator
      </Button>
    </div>
  </div>

  <div class="mt-6 grid gap-3 md:grid-cols-4">
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Tracked</p>
      <p class="mt-2 text-2xl font-bold text-white">{trackedCount}</p>
      <p class="mt-1 text-xs text-slate-500">creator sources</p>
    </div>
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Scanning</p>
      <p class="mt-2 text-2xl font-bold text-white">{scanningCount}</p>
      <p class="mt-1 text-xs text-slate-500">enabled for discovery</p>
    </div>
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Platforms</p>
      <p class="mt-2 text-2xl font-bold text-white">{platformCount}</p>
      <p class="mt-1 text-xs text-slate-500">distinct providers</p>
    </div>
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Scanned</p>
      <p class="mt-2 text-2xl font-bold text-white">{scannedCount}</p>
      <p class="mt-1 text-xs text-slate-500">completed at least one scan</p>
    </div>
  </div>

  {#if loadError || actionError}
    <div
      class="mt-5 flex items-start gap-3 rounded-xl border border-red-900/60 bg-red-950/35 p-4 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionError ?? loadError}</span>
    </div>
  {/if}

  {#if actionNotice}
    <div
      class="mt-5 flex items-start gap-3 rounded-xl border border-emerald-900/60 bg-emerald-950/35 p-4 text-sm text-emerald-300"
      role="status"
    >
      <RefreshOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{actionNotice}</span>
    </div>
  {/if}

  {#if loading && sources.length === 0}
    <div class="mt-16 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if sources.length === 0}
    <div class="mt-8 rounded-xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <UsersGroupOutline class="mx-auto h-10 w-10 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No creators tracked yet</p>
      <p class="mt-1 text-sm text-slate-500">
        Track a channel to automatically discover and download everything it uploads.
      </p>
      <Button
        color="blue"
        onclick={openCreateForm}
        class="mt-5 border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400!"
      >
        <CirclePlusOutline class="mr-1.5 h-4 w-4" />
        Track your first creator
      </Button>
    </div>
  {:else}
    <ul class="mt-6 space-y-3">
      {#each sources as source (source.id)}
        {@const busyAction = busy[source.id]}
        <li
          class={[
            'rounded-2xl border p-5',
            source.scanEnabled ? 'border-slate-800/90 bg-slate-900/45' : 'border-slate-800/60 bg-slate-900/25'
          ]}
        >
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="truncate text-base font-semibold text-slate-100">{displayName(source)}</h2>
                <Badge
                  rounded
                  color="gray"
                  class="bg-slate-800! px-2! py-0.5! text-[10px]! font-bold! uppercase! tracking-wide! text-slate-300!"
                >
                  {source.platform}
                </Badge>
                <Badge
                  rounded
                  color="gray"
                  class="bg-slate-800! px-2! py-0.5! text-[10px]! font-bold! text-slate-400!"
                >
                  {source.sourceType}
                </Badge>
                <span
                  class={[
                    'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1',
                    source.scanEnabled
                      ? 'bg-emerald-500/12 text-emerald-300 ring-emerald-500/20'
                      : 'bg-slate-500/12 text-slate-400 ring-slate-500/20'
                  ]}
                >
                  {source.scanEnabled ? 'SCANNING' : 'PAUSED'}
                </span>
              </div>
              <a
                href={source.sourceUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="mt-1.5 inline-flex max-w-full items-center gap-1.5 text-xs text-slate-500 transition hover:text-blue-400"
              >
                <LinkOutline class="h-3.5 w-3.5 shrink-0" />
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
                  <Spinner size="4" />
                {:else if source.scanEnabled}
                  <PauseOutline class="h-4 w-4" />
                {:else}
                  <PlayOutline class="h-4 w-4" />
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
                  <Spinner size="4" />
                {:else}
                  <ImageOutline class="h-4 w-4" />
                {/if}
                Refresh assets
              </button>
              <button
                type="button"
                class={rowActionClass}
                disabled={Boolean(busyAction)}
                onclick={() => openEditForm(source)}
              >
                <EditOutline class="h-4 w-4" />
                Edit
              </button>
              <button
                type="button"
                class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-300 disabled:opacity-50"
                disabled={Boolean(busyAction)}
                onclick={() => requestDelete(source)}
              >
                <TrashBinOutline class="h-4 w-4" />
                Delete
              </button>
            </div>
          </div>

          <dl class="mt-4 grid gap-x-6 gap-y-2 text-xs sm:grid-cols-2 lg:grid-cols-4">
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-slate-600">Last scan</dt>
              <dd class="mt-0.5 text-slate-300">{formatScan(source.lastSuccessfulScanAt)}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-slate-600">Last full scan</dt>
              <dd class="mt-0.5 text-slate-300">{formatScan(source.lastFullScanAt)}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-slate-600">Full rescan</dt>
              <dd class="mt-0.5 text-slate-300">every {source.fullRescanIntervalDays} days</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-slate-600">Scan paging</dt>
              <dd class="mt-0.5 text-slate-300">
                {source.incrementalPageSize} per page · stop after {source.consecutiveKnownThreshold} known
              </dd>
            </div>
          </dl>

          <div class="mt-4 border-t border-slate-800/70 pt-3">
            <button
              type="button"
              class="inline-flex items-center gap-1.5 text-xs font-semibold text-slate-500 transition hover:text-blue-400"
              onclick={() => toggleIgnored(source)}
            >
              <EyeSlashOutline class="h-3.5 w-3.5" />
              {expandedIgnoredId === source.id ? 'Hide ignored videos' : 'Show ignored videos'}
            </button>

            {#if expandedIgnoredId === source.id}
              <div class="mt-3">
                {#if ignoredLoading}
                  <div class="flex justify-center py-6">
                    <Spinner size="6" />
                  </div>
                {:else if ignoredError}
                  <p class="text-xs text-red-300">{ignoredError}</p>
                {:else if ignoredItems.length === 0}
                  <p class="text-xs text-slate-500">
                    No videos have been suppressed by ignore keywords for this creator.
                  </p>
                {:else}
                  <ul class="space-y-2">
                    {#each ignoredItems as item (item.id)}
                      <li
                        class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-800/70 bg-slate-950/40 px-3 py-2"
                      >
                        <div class="min-w-0">
                          <a
                            href={item.canonicalUrl}
                            target="_blank"
                            rel="noopener noreferrer"
                            class="block truncate text-xs font-medium text-slate-300 transition hover:text-blue-400"
                          >
                            {item.title?.trim() || item.canonicalUrl}
                          </a>
                          <p class="mt-0.5 text-[11px] text-slate-600">
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
                            class="inline-flex items-center gap-1 rounded-full bg-amber-500/10 px-2 py-0.5 text-[10px] font-bold text-amber-300 ring-1 ring-amber-500/20"
                          >
                            <BanOutline class="h-3 w-3" />
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

<Modal
  bind:open={formOpen}
  title={editingSource ? 'Edit creator source' : 'Track a creator'}
  size="lg"
  class="z-50"
>
  <form id="creator-source-form" class="space-y-4" onsubmit={submitForm}>
    <div>
      <Label for="creator-source-url" class="mb-1.5 text-xs font-semibold text-slate-400">
        Channel URL
      </Label>
      <Input
        id="creator-source-url"
        type="url"
        required
        bind:value={form.sourceUrl}
        placeholder="https://www.youtube.com/@creator/videos"
        class={fieldClass}
      />
      <p class="mt-1.5 text-[11px] text-slate-600">
        The channel or listing page that discovery scans will page through for new uploads.
      </p>
    </div>

    <div class="grid gap-4 sm:grid-cols-2">
      <div>
        <Label for="creator-platform" class="mb-1.5 text-xs font-semibold text-slate-400">Platform</Label>
        <Input
          id="creator-platform"
          required
          bind:value={form.platform}
          placeholder="youtube"
          class={fieldClass}
        />
      </div>
      <div>
        <Label for="creator-source-type" class="mb-1.5 text-xs font-semibold text-slate-400">
          Content type
        </Label>
        <Select
          id="creator-source-type"
          items={sourceTypeOptions}
          bind:value={form.sourceType}
          class={fieldClass}
        />
      </div>
    </div>

    <div class="grid gap-4 sm:grid-cols-2">
      <div>
        <Label for="creator-page-size" class="mb-1.5 text-xs font-semibold text-slate-400">
          Incremental page size
        </Label>
        <Input
          id="creator-page-size"
          type="number"
          min="1"
          max="500"
          required
          bind:value={form.incrementalPageSize}
          class={fieldClass}
        />
        <p class="mt-1.5 text-[11px] text-slate-600">Videos fetched per page during a scan (1-500).</p>
      </div>
      <div>
        <Label for="creator-known-threshold" class="mb-1.5 text-xs font-semibold text-slate-400">
          Consecutive known threshold
        </Label>
        <Input
          id="creator-known-threshold"
          type="number"
          min="1"
          max="500"
          required
          bind:value={form.consecutiveKnownThreshold}
          class={fieldClass}
        />
        <p class="mt-1.5 text-[11px] text-slate-600">
          Stop scanning after this many already-known videos in a row (1-500).
        </p>
      </div>
      <div>
        <Label for="creator-rescan-days" class="mb-1.5 text-xs font-semibold text-slate-400">
          Full rescan interval (days)
        </Label>
        <Input
          id="creator-rescan-days"
          type="number"
          min="1"
          max="365"
          required
          bind:value={form.fullRescanIntervalDays}
          class={fieldClass}
        />
        <p class="mt-1.5 text-[11px] text-slate-600">How often the entire channel is re-walked (1-365).</p>
      </div>
      <div>
        <Label for="creator-metadata-window" class="mb-1.5 text-xs font-semibold text-slate-400">
          Metadata refresh window
        </Label>
        <Input
          id="creator-metadata-window"
          type="number"
          min="1"
          max="500"
          required
          bind:value={form.metadataRefreshWindow}
          class={fieldClass}
        />
        <p class="mt-1.5 text-[11px] text-slate-600">
          Recent videos whose metadata is refreshed on each scan (1-500).
        </p>
      </div>
    </div>

    <div class="flex items-center justify-between rounded-xl border border-slate-800/80 bg-slate-950/40 px-4 py-3">
      <div>
        <p class="text-sm font-semibold text-slate-200">Automatic scanning</p>
        <p class="mt-0.5 text-xs text-slate-500">
          Discover and download new uploads from this creator on the recurring schedule.
        </p>
      </div>
      <Toggle bind:checked={form.scanEnabled} />
    </div>

    {#if formError}
      <div class="flex items-start gap-2 rounded-lg border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{formError}</span>
      </div>
    {/if}
  </form>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <Button
        color="dark"
        class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
        disabled={formBusy}
        onclick={() => (formOpen = false)}
      >
        Cancel
      </Button>
      <Button
        type="submit"
        form="creator-source-form"
        color="blue"
        class="border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
        disabled={formBusy}
      >
        {#if formBusy}
          <Spinner size="4" class="mr-1.5" />
        {/if}
        {editingSource ? 'Save changes' : 'Track creator'}
      </Button>
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
