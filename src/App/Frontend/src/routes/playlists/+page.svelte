<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { Modal, Select } from '$lib/components/ui';
  import {
    Ban,
    ChevronDown,
    ChevronUp,
    CircleAlert,
    CirclePlus,
    ClipboardList,
    Download,
    Link,
    Play,
    RefreshCw
  } from '@lucide/svelte';
  import {
    forceQueuePlaylistItem,
    getPlatformPlaylist,
    listPlatformPlaylists,
    queuePlaylistDownload,
    type PlatformPlaylist,
    type PlatformPlaylistItem
  } from '$lib/api/playlists';
  import { listDownloadConfigSets, type DownloadConfigSet } from '$lib/api/downloadConfigSets';
  import TargetNotePanel from '$lib/components/TargetNotePanel.svelte';
  import { formatRelativeDate } from '$lib/media';


  let playlists = $state<PlatformPlaylist[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let actionError = $state<string | null>(null);
  let actionNotice = $state<string | null>(null);

  let submitOpen = $state(false);
  let submitBusy = $state(false);
  let submitError = $state<string | null>(null);
  let submitUrl = $state('');
  let submitStorageKey = $state('default');
  let submitConfigSetKey = $state('');
  let submitFetchComments = $state(false);
  let submitOptionsLoaded = $state(false);
  let storageOptions = $state<Array<{ value: string; name: string }>>([
    { value: 'default', name: 'default' }
  ]);
  let configSets = $state<DownloadConfigSet[]>([]);

  const configSetOptions = $derived([
    { value: '', name: 'None (use per-request settings)' },
    ...configSets.map((set) => ({ value: set.key, name: set.name }))
  ]);

  let expandedId = $state<string | null>(null);
  let detail = $state<PlatformPlaylist | null>(null);
  let detailLoading = $state(false);
  let detailError = $state<string | null>(null);
  let forceQueueBusy = $state<Record<string, boolean>>({});

  const totalCount = $derived(playlists.length);
  const inProgressCount = $derived(
    playlists.filter((playlist) => !playlist.completedAt && playlist.state !== 'Failed').length
  );
  const completedItemCount = $derived(playlists.reduce((sum, playlist) => sum + playlist.completedItems, 0));
  const failedItemCount = $derived(playlists.reduce((sum, playlist) => sum + playlist.failedItems, 0));

  onMount(() => {
    void loadPlaylists();
  });

  async function loadPlaylists() {
    loading = true;
    loadError = null;
    try {
      playlists = await listPlatformPlaylists();
      const requestedPlaylistId = page.url.searchParams.get('playlist');
      if (
        requestedPlaylistId &&
        expandedId !== requestedPlaylistId &&
        playlists.some((playlist) => playlist.playlistId === requestedPlaylistId)
      ) {
        const requestedPlaylist = playlists.find((playlist) => playlist.playlistId === requestedPlaylistId);
        if (requestedPlaylist) {
          await toggleDetail(requestedPlaylist);
        }
      }
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load playlists.';
    } finally {
      loading = false;
    }
  }

  function openSubmitModal() {
    submitError = null;
    submitUrl = '';
    submitOpen = true;
    if (!submitOptionsLoaded) {
      submitOptionsLoaded = true;
      void loadSubmitOptions();
    }
  }

  async function loadSubmitOptions() {
    try {
      const response = await fetch('/api/storage/list');
      if (response.ok) {
        const items = (await response.json()) as { key: string; description?: string | null }[];
        if (items.length > 0) {
          storageOptions = items.map((item) => ({
            value: item.key,
            name: item.description ? `${item.key} — ${item.description}` : item.key
          }));
          if (!storageOptions.some((option) => option.value === submitStorageKey)) {
            submitStorageKey = storageOptions[0].value;
          }
        }
      }
    } catch {
      // Keep the default storage key when the list is unavailable.
    }
    try {
      configSets = await listDownloadConfigSets();
    } catch {
      // Config sets are optional; playlist downloads work without them.
    }
  }

  async function submitPlaylist(event: SubmitEvent) {
    event.preventDefault();
    const sourceUrl = submitUrl.trim();
    if (!sourceUrl) {
      submitError = 'Playlist URL is required.';
      return;
    }

    submitBusy = true;
    submitError = null;
    try {
      await queuePlaylistDownload({
        sourceUrl,
        storageKey: submitStorageKey.trim() || 'default',
        configSetKey: submitConfigSetKey || null,
        fetchComments: submitFetchComments
      });
      actionNotice = 'Playlist download queued. It will appear below once metadata resolves.';
      actionError = null;
      submitOpen = false;
      await loadPlaylists();
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Could not queue the playlist download.';
    } finally {
      submitBusy = false;
    }
  }

  async function toggleDetail(playlist: PlatformPlaylist) {
    if (expandedId === playlist.playlistId) {
      expandedId = null;
      detail = null;
      return;
    }
    expandedId = playlist.playlistId;
    detail = null;
    detailError = null;
    detailLoading = true;
    try {
      const loaded = await getPlatformPlaylist(playlist.playlistId);
      if (expandedId === playlist.playlistId) {
        detail = loaded;
      }
    } catch (err) {
      if (expandedId === playlist.playlistId) {
        detailError = err instanceof Error ? err.message : 'Could not load the playlist items.';
      }
    } finally {
      if (expandedId === playlist.playlistId) {
        detailLoading = false;
      }
    }
  }

  async function forceQueue(playlistId: string, item: PlatformPlaylistItem) {
    actionError = null;
    actionNotice = null;
    forceQueueBusy = { ...forceQueueBusy, [item.jobId]: true };
    try {
      await forceQueuePlaylistItem(playlistId, item.jobId);
      actionNotice = `Re-queued "${item.entryTitle ?? item.entryUrl}" with force enabled.`;
      if (expandedId === playlistId) {
        detail = await getPlatformPlaylist(playlistId);
      }
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Could not force-queue the playlist entry.';
    } finally {
      const { [item.jobId]: _removed, ...rest } = forceQueueBusy;
      forceQueueBusy = rest;
    }
  }

  function displayTitle(playlist: PlatformPlaylist): string {
    return playlist.title?.trim() || compactUrl(playlist.sourceUrl);
  }

  function compactUrl(sourceUrl: string): string {
    try {
      const url = new URL(sourceUrl);
      return `${url.hostname.replace(/^www\./, '')}${url.pathname === '/' ? '' : url.pathname}`;
    } catch {
      return sourceUrl;
    }
  }

  function progressPercent(playlist: PlatformPlaylist): number {
    if (playlist.totalItems <= 0) {
      return 0;
    }
    const settled = playlist.completedItems + playlist.failedItems;
    return Math.min(100, Math.round((settled / playlist.totalItems) * 100));
  }

  function stateLabel(playlist: PlatformPlaylist): string {
    switch (playlist.state) {
      case 'PendingMetadata':
        return 'RESOLVING';
      case 'MetadataResolved':
        return playlist.completedAt ? 'COMPLETE' : 'DOWNLOADING';
      case 'Failed':
        return 'FAILED';
      default:
        return playlist.state.toUpperCase();
    }
  }

  function stateTone(playlist: PlatformPlaylist): string {
    switch (stateLabel(playlist)) {
      case 'COMPLETE':
        return 'bg-success/12 text-success ring-success/20';
      case 'FAILED':
        return 'bg-error/12 text-error ring-error/25';
      case 'RESOLVING':
        return 'bg-warning/12 text-warning ring-warning/20';
      default:
        return 'bg-primary/12 text-primary ring-primary/20';
    }
  }

  function itemStateTone(status: string): string {
    const normalized = status.toLowerCase();
    if (['completed', 'completedwithwarnings', 'alreadydownloaded', 'ignored'].includes(normalized)) {
      return 'bg-success/12 text-success ring-success/20';
    }
    if (normalized === 'failed') {
      return 'bg-error/12 text-error ring-error/25';
    }
    if (normalized === 'stopped') {
      return 'bg-base-200/12 text-base-content/60 ring-base-300/20';
    }
    return 'bg-primary/12 text-primary ring-primary/20';
  }

  function isIgnored(item: PlatformPlaylistItem): boolean {
    return item.jobStatus.toLowerCase() === 'ignored';
  }
</script>

<svelte:head>
  <title>Playlists · FrostStream</title>
</svelte:head>

<section aria-labelledby="playlists-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="playlists-title" class="text-2xl font-bold tracking-tight text-base-content">Playlists</h1>
      <p class="mt-1 text-sm text-base-content/50">
        Playlist downloads ingested from providers, with per-item progress
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <button class="btn btn-sm btn-neutral text-xs" onclick={loadPlaylists} disabled={loading}>
        {#if loading}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <RefreshCw class="mr-1.5 h-4 w-4" />
        {/if}
        Refresh
      </button>
      <button class="btn btn-sm btn-primary text-xs" onclick={openSubmitModal}>
        <CirclePlus class="mr-1.5 h-4 w-4" />
        Download playlist
      </button>
    </div>
  </div>

  <div class="mt-6 grid gap-3 md:grid-cols-4">
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Playlists</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{totalCount}</p>
      <p class="mt-1 text-xs text-base-content/50">download requests</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">In progress</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{inProgressCount}</p>
      <p class="mt-1 text-xs text-base-content/50">resolving or downloading</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Items done</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{completedItemCount}</p>
      <p class="mt-1 text-xs text-base-content/50">videos completed</p>
    </div>
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Items failed</p>
      <p class="mt-2 text-2xl font-bold text-base-content">{failedItemCount}</p>
      <p class="mt-1 text-xs text-base-content/50">across all playlists</p>
    </div>
  </div>

  {#if loadError || actionError}
    <div
      class="mt-5 flex items-start gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
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

  {#if loading && playlists.length === 0}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if playlists.length === 0}
    <div class="mt-8 rounded-xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <ClipboardList class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No playlists downloaded yet</p>
      <p class="mt-1 text-sm text-base-content/50">
        Queue a playlist URL and every entry will be downloaded into your library.
      </p>
      <button class="btn btn-sm btn-primary mt-5 text-xs" onclick={openSubmitModal}>
        <CirclePlus class="mr-1.5 h-4 w-4" />
        Download your first playlist
      </button>
    </div>
  {:else}
    <ul class="mt-6 space-y-3">
      {#each playlists as playlist (playlist.playlistId)}
        {@const expanded = expandedId === playlist.playlistId}
        <li class="rounded-2xl border border-base-300/90 bg-base-200/45 p-5">
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="truncate text-base font-semibold text-base-content">{displayTitle(playlist)}</h2>
                <span
                  class={[
                    'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[10px] font-bold ring-1',
                    stateTone(playlist)
                  ]}
                >
                  {stateLabel(playlist)}
                </span>
                {#if playlist.totalItems > 0}
                  <span class="badge badge-sm badge-ghost rounded-full text-[10px]">
                    {playlist.totalItems} items
                  </span>
                {/if}
              </div>
              <a
                href={playlist.sourceUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="mt-1.5 inline-flex max-w-full items-center gap-1.5 text-xs text-base-content/50 transition hover:text-primary"
              >
                <Link class="h-3.5 w-3.5 shrink-0" />
                <span class="truncate">{compactUrl(playlist.sourceUrl)}</span>
              </a>
            </div>

            <button
              type="button"
              class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary"
              onclick={() => toggleDetail(playlist)}
            >
              {#if expanded}
                <ChevronUp class="h-4 w-4" />
                Hide items
              {:else}
                <ChevronDown class="h-4 w-4" />
                Show items
              {/if}
            </button>
          </div>

          <div class="mt-4">
            <div class="flex items-center justify-between text-xs text-base-content/50">
              <span>
                {playlist.completedItems} done
                {#if playlist.failedItems > 0}
                  · <span class="text-error">{playlist.failedItems} failed</span>
                {/if}
                {#if playlist.pendingItems > 0}
                  · {playlist.pendingItems} pending
                {/if}
              </span>
              <span>{progressPercent(playlist)}%</span>
            </div>
            <div class="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-base-300">
              <div
                class={['h-full rounded-full', playlist.failedItems > 0 ? 'bg-warning' : 'bg-primary']}
                style={`width: ${progressPercent(playlist)}%`}
              ></div>
            </div>
          </div>

          <dl class="mt-4 grid gap-x-6 gap-y-2 text-xs sm:grid-cols-2 lg:grid-cols-4">
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Requested</dt>
              <dd class="mt-0.5 text-base-content/80">{formatRelativeDate(playlist.createdAt) ?? '-'}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Updated</dt>
              <dd class="mt-0.5 text-base-content/80">{formatRelativeDate(playlist.updatedAt) ?? '-'}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Completed</dt>
              <dd class="mt-0.5 text-base-content/80">{formatRelativeDate(playlist.completedAt) ?? 'not yet'}</dd>
            </div>
            <div>
              <dt class="font-bold uppercase tracking-[0.08em] text-base-content/40">Last scanned</dt>
              <dd class="mt-0.5 text-base-content/80">{formatRelativeDate(playlist.lastScannedAt) ?? 'never'}</dd>
            </div>
          </dl>

          <div class="mt-4">
            <TargetNotePanel
              targetType="playlist"
              targetId={playlist.playlistId}
              targetLabel="Playlist"
              initialNote={playlist.userNote}
              onChange={(note) => {
                playlists = playlists.map((item) =>
                  item.playlistId === playlist.playlistId ? { ...item, userNote: note } : item
                );
                if (detail?.playlistId === playlist.playlistId) {
                  detail = { ...detail, userNote: note };
                }
              }}
            />
          </div>

          {#if expanded}
            <div class="mt-4 border-t border-base-300/70 pt-4">
              {#if detailLoading}
                <div class="flex justify-center py-6">
                  <span class="loading loading-spinner loading-sm"></span>
                </div>
              {:else if detailError}
                <p class="text-xs text-error">{detailError}</p>
              {:else if !detail?.items || detail.items.length === 0}
                <p class="text-xs text-base-content/50">No items discovered for this playlist yet.</p>
              {:else}
                <ul class="space-y-2">
                  {#each detail.items as item (item.jobId)}
                    <li
                      class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-base-300/70 bg-base-200/40 px-3 py-2"
                    >
                      <div class="flex min-w-0 items-center gap-3">
                        <span class="w-8 shrink-0 text-right font-mono text-[11px] text-base-content/40">
                          {item.playlistIndex}
                        </span>
                        <div class="min-w-0">
                          {#if item.mediaGuid}
                            <a
                              href={`/watch/${item.mediaGuid}`}
                              class="block truncate text-xs font-medium text-base-content/80 transition hover:text-primary"
                            >
                              {item.entryTitle?.trim() || item.entryUrl}
                            </a>
                          {:else}
                            <a
                              href={item.entryUrl}
                              target="_blank"
                              rel="noopener noreferrer"
                              class="block truncate text-xs font-medium text-base-content/80 transition hover:text-primary"
                            >
                              {item.entryTitle?.trim() || item.entryUrl}
                            </a>
                          {/if}
                        </div>
                      </div>
                      <div class="flex shrink-0 items-center gap-2">
                        {#if item.ignoredKeyword}
                          <span
                            class="inline-flex items-center gap-1 rounded-full bg-warning/10 px-2 py-0.5 text-[10px] font-bold text-warning ring-1 ring-warning/20"
                          >
                            <Ban class="h-3 w-3" />
                            {item.ignoredKeyword}
                          </span>
                        {/if}
                        <span
                          class={['rounded-full px-2 py-0.5 text-[10px] font-bold ring-1', itemStateTone(item.jobStatus)]}
                        >
                          {item.jobStatus}
                        </span>
                        {#if isIgnored(item)}
                          <button
                            type="button"
                            class="inline-flex h-7 items-center gap-1 rounded-lg border border-base-content/20 bg-base-200/70 px-2 text-[11px] font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary disabled:opacity-50"
                            disabled={Boolean(forceQueueBusy[item.jobId])}
                            onclick={() => forceQueue(playlist.playlistId, item)}
                            title="Download this entry anyway, ignoring the keyword filter"
                          >
                            {#if forceQueueBusy[item.jobId]}
                              <span class="loading loading-spinner loading-xs"></span>
                            {:else}
                              <Play class="h-3 w-3" />
                            {/if}
                            Download anyway
                          </button>
                        {/if}
                      </div>
                    </li>
                  {/each}
                </ul>
              {/if}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
</section>

<Modal bind:open={submitOpen} title="Queue playlist download" size="md">
  <form id="playlist-download-form" class="space-y-4" onsubmit={submitPlaylist}>
    <div>
      <label class="label mb-1.5 text-xs" for="playlist-url">Playlist URL</label>
      <input class="input w-full" id="playlist-url" type="url" required
         bind:value={submitUrl} placeholder="https://www.youtube.com/playlist?list=..." />
      <p class="mt-1.5 text-[11px] text-base-content/40">
        Every entry in the playlist is queued as its own download job. Videos already in the library are
        skipped.
      </p>
    </div>

    <div>
      <label class="label mb-1.5 text-xs" for="playlist-storage">
        Storage target
      </label>
      <Select id="playlist-storage" items={storageOptions} bind:value={submitStorageKey} />
    </div>

    <div>
      <label class="label mb-1.5 text-xs" for="playlist-config-set">Config set</label>
      <Select id="playlist-config-set" items={configSetOptions} bind:value={submitConfigSetKey} />
      <p class="mt-1.5 text-[11px] text-base-content/40">
        Applies its saved yt-dlp options and ignore keywords to every playlist entry.
      </p>
    </div>

    <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={submitFetchComments} /><span>Fetch comments</span></label>

    {#if submitError}
      <div
        class="flex items-start gap-2 rounded-lg border border-error/30 bg-error/10 p-3 text-sm text-error"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{submitError}</span>
      </div>
    {/if}
  </form>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" disabled={submitBusy} onclick={() => (submitOpen = false)}>
        Cancel
      </button>
      <button class="btn btn-sm btn-primary text-xs" type="submit" form="playlist-download-form" disabled={submitBusy}>
        {#if submitBusy}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <Download class="mr-1.5 h-4 w-4" />
        {/if}
        Queue download
      </button>
    </div>
  {/snippet}
</Modal>
