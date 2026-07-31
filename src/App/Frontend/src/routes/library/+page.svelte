<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    ChevronLeft,
    ChevronRight,
    CircleAlert,
    LayoutList,
    ListMusic,
    Pen,
    Play,
    Plus
  } from '@lucide/svelte';
  import { getUserPlaylist, listUserPlaylists, type UserPlaylist } from '$lib/api/userPlaylists';
  import {
    listProviderPlaylistLibrary,
    type ProviderPlaylistLibraryItem
  } from '$lib/api/playlists';
  import {
    accentFor,
    formatBytes,
    formatDuration,
    formatRelativeDate,
    formatViews,
    initialsFor
  } from '$lib/media';
  import { listWatchHistory, type WatchState } from '$lib/api/watchState';
  import { listLikedMedia } from '$lib/api/mediaLikes';

  interface MediaCard {
    mediaGuid: string;
    title: string;
    thumbnailStoragePath?: string | null;
    durationSeconds?: number | null;
    releaseDate?: string | null;
    viewCount?: number | null;
    wasLive: boolean;
    account: {
      accountId: number;
      platform: string;
      accountName: string;
      accountHandle: string;
    };
  }

  interface PagedResponse {
    items: MediaCard[];
    page: number;
    totalCount: number;
    hasMore: boolean;
  }

  interface Overview {
    inventory: {
      totalMedia: number;
      totalChannels: number;
      totalPlaylists: number;
      totalDownloads: number;
      totalBytes: number;
      totalDurationSeconds: number;
    };
    watchProgress: {
      watchedCount: number;
      watchedPercent: number;
    };
  }

  const pageSize = 24;
  const tabs = ['Videos', 'Playlists', 'History', 'Liked'] as const;
  type Tab = (typeof tabs)[number];

  const sortOptions = [
    { value: 'added_at:desc', name: 'Recently added' },
    { value: 'added_at:asc', name: 'Oldest added' },
    { value: 'release_date:desc', name: 'Release date (newest)' },
    { value: 'release_date:asc', name: 'Release date (oldest)' },
    { value: 'title:asc', name: 'Title A–Z' },
    { value: 'view_count:desc', name: 'Most viewed' },
    { value: 'duration:desc', name: 'Longest first' }
  ];

  interface UserPlaylistCard {
    playlist: UserPlaylist;
    firstGuid: string | null;
  }

  interface PlatformPlaylistCard {
    playlist: ProviderPlaylistLibraryItem;
  }

  let sort = $state('added_at:desc');
  let playlistsRequested = $state(false);
  let userPlaylistCards = $state<UserPlaylistCard[]>([]);
  let userPlaylistsLoading = $state(false);
  let userPlaylistsError = $state<string | null>(null);
  let platformPlaylistCards = $state<PlatformPlaylistCard[]>([]);
  let platformPlaylistsLoading = $state(false);
  let platformPlaylistsError = $state<string | null>(null);
  let currentPage = $state(1);
  let items = $state<MediaCard[]>([]);
  let totalCount = $state(0);
  let hasMore = $state(false);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let needsLogin = $state(false);
  let overview = $state<Overview | null>(null);
  let historyStates = $state<Record<string, WatchState>>({});
  let likedAtByGuid = $state<Record<string, string>>({});

  const totalPages = $derived(Math.max(1, Math.ceil(totalCount / pageSize)));
  const tabFromQuery = $derived.by(() => {
    const value = page.url.searchParams.get('tab');
    return value === 'Videos' || value === 'Playlists' || value === 'History' || value === 'Liked'
      ? value
      : null;
  });
  // The URL is the single source of truth for which tab is active — selectTab() only ever
  // navigates, it never mutates a separate local copy. That was the bug: a local `activeTab`
  // $state was set on click, but a reactive effect kept snapping it back to whatever the URL
  // still said, since nothing told the URL about the click (most visibly, clicking back to the
  // default "Videos" tab never cleared the leftover ?tab=History from a previous click).
  const activeTab = $derived(tabFromQuery ?? 'Videos');

  onMount(() => {
    void loadOverview();
  });

  $effect(() => {
    const tab = activeTab;
    if (tab === 'Playlists' && !playlistsRequested) {
      playlistsRequested = true;
      void loadUserPlaylistCards();
      void loadPlatformPlaylistCards();
      return;
    }
    if (tab === 'Videos' || tab === 'History' || tab === 'Liked') {
      void loadPage(1);
    }
  });

  async function loadOverview() {
    try {
      const response = await fetch('/api/global/statistics/overview');
      if (response.ok) {
        overview = (await response.json()) as Overview;
      }
    } catch {
      // Stat cards are decorative; the grid is the page's real content.
    }
  }

  async function loadPage(target: number) {
    loading = true;
    loadError = null;

    try {
      if (activeTab === 'History') {
        const data = await listWatchHistory(target, pageSize);
        items = data.items.map((item) => item.media);
        historyStates = Object.fromEntries(data.items.map((item) => [item.media.mediaGuid, item.watchState]));
        likedAtByGuid = {};
        currentPage = data.page;
        totalCount = data.totalCount;
        hasMore = data.hasMore;
        return;
      }

      if (activeTab === 'Liked') {
        const data = await listLikedMedia(target, pageSize);
        items = data.items.map((item) => item.media);
        likedAtByGuid = Object.fromEntries(
          data.items
            .filter((item) => item.like.likedAt)
            .map((item) => [item.media.mediaGuid, item.like.likedAt as string])
        );
        historyStates = {};
        currentPage = data.page;
        totalCount = data.totalCount;
        hasMore = data.hasMore;
        return;
      }

      historyStates = {};
      likedAtByGuid = {};
      const [sortBy, sortOrder] = sort.split(':');
      const query = new URLSearchParams({
        page: String(target),
        pageSize: String(pageSize),
        sortBy,
        sortOrder
      });
      const response = await fetch(`/api/metadata?${query}`);
      if (response.status === 401) {
        needsLogin = true;
        loadError = 'Your session has expired.';
        return;
      }
      if (!response.ok) {
        loadError = `Could not load your library (status ${response.status}).`;
        return;
      }
      const data = (await response.json()) as PagedResponse;
      items = data.items;
      currentPage = data.page;
      totalCount = data.totalCount;
      hasMore = data.hasMore;
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load your library.';
    } finally {
      loading = false;
    }
  }

  function changeSort() {
    void loadPage(1);
  }

  function selectTab(tab: Tab) {
    const url = new URL(page.url);
    if (tab === 'Videos') {
      url.searchParams.delete('tab');
    } else {
      url.searchParams.set('tab', tab);
    }
    void goto(`${url.pathname}${url.search}`, { replaceState: true, noScroll: true, keepFocus: true });
  }

  async function loadUserPlaylistCards() {
    userPlaylistsLoading = true;
    userPlaylistsError = null;
    try {
      const list = await listUserPlaylists();
      // The list endpoint omits items; each detail supplies the first video for the
      // card thumbnail and the ?ulist= entry point.
      const details = await Promise.all(
        list.map((playlist) => getUserPlaylist(playlist.playlistId).catch(() => playlist))
      );
      userPlaylistCards = details.map((playlist) => ({
        playlist,
        firstGuid: playlist.items?.[0]?.mediaGuid ?? null
      }));
    } catch (err) {
      userPlaylistsError = err instanceof Error ? err.message : 'Could not load your playlists.';
    } finally {
      userPlaylistsLoading = false;
    }
  }

  async function loadPlatformPlaylistCards() {
    platformPlaylistsLoading = true;
    platformPlaylistsError = null;
    try {
      const list = await listProviderPlaylistLibrary();
      platformPlaylistCards = list.map((playlist) => ({ playlist }));
    } catch (err) {
      platformPlaylistsError = err instanceof Error ? err.message : 'Could not load downloaded playlists.';
    } finally {
      platformPlaylistsLoading = false;
    }
  }

  function userPlaylistMeta(card: UserPlaylistCard): string {
    const count = card.playlist.itemCount;
    return [
      `${count} ${count === 1 ? 'video' : 'videos'}`,
      formatRelativeDate(card.playlist.updatedAt) ? `updated ${formatRelativeDate(card.playlist.updatedAt)}` : null
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function platformPlaylistMeta(card: PlatformPlaylistCard): string {
    const playlist = card.playlist;
    return [
      `${playlist.itemCount} ${playlist.itemCount === 1 ? 'video' : 'videos'}`
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function metaLine(card: MediaCard): string {
    return [formatViews(card.viewCount), card.wasLive ? 'was live' : null]
      .filter(Boolean)
      .join(' · ');
  }

  function cardHref(card: MediaCard): string {
    if (activeTab === 'History') {
      const state = historyStates[card.mediaGuid];
      const position = state?.positionSeconds ?? 0;
      if (position > 0 && !state?.completed) {
        return `/watch/${card.mediaGuid}?t=${Math.floor(position)}`;
      }
    }
    return `/watch/${card.mediaGuid}`;
  }

  function cardMetaLine(card: MediaCard): string {
    if (activeTab === 'History') {
      const state = historyStates[card.mediaGuid];
      return [
        state?.positionSeconds ? `at ${formatDuration(state.positionSeconds)}` : null,
        state?.completed ? 'watched' : null,
        state?.lastPlayedAt ? formatRelativeDate(state.lastPlayedAt) : null
      ]
        .filter(Boolean)
        .join(' · ');
    }

    if (activeTab === 'Liked') {
      return [likedAtByGuid[card.mediaGuid] ? `liked ${formatRelativeDate(likedAtByGuid[card.mediaGuid])}` : null]
        .filter(Boolean)
        .join(' · ');
    }

    return [metaLine(card), formatRelativeDate(card.releaseDate)].filter(Boolean).join(' · ');
  }

  function gridSummary(): string {
    if (loading) {
      return 'Loading...';
    }
    if (activeTab === 'History') {
      return `${totalCount} ${totalCount === 1 ? 'history item' : 'history items'}`;
    }
    if (activeTab === 'Liked') {
      return `${totalCount} liked ${totalCount === 1 ? 'title' : 'titles'}`;
    }
    return `${totalCount} ${totalCount === 1 ? 'title' : 'titles'} on the server`;
  }

  function emptyTitle(): string {
    if (activeTab === 'History') {
      return 'No watch history yet';
    }
    if (activeTab === 'Liked') {
      return 'No liked videos yet';
    }
    return 'No titles yet';
  }

  function emptyDescription(): string {
    if (activeTab === 'History') {
      return 'Start a video and it will appear here.';
    }
    if (activeTab === 'Liked') {
      return 'Like videos from the watch page and they will show up here.';
    }
    return 'Queue something from the Download page and it will show up here once processed.';
  }

  function thumbnailUrl(card: MediaCard): string | null {
    return card.thumbnailStoragePath ? `/api/media/watch/${card.mediaGuid}/thumbnail` : null;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<svelte:head>
  <title>Library · FrostStream</title>
</svelte:head>

<section aria-labelledby="library-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="library-title" class="text-2xl font-bold tracking-tight text-base-content">Library</h1>
      <p class="mt-1 text-sm text-base-content/50">Your playlists, saved videos, and files on this server.</p>
    </div>
    <a class="btn btn-sm btn-neutral text-xs" href="/profile/playlists">
      <Plus class="mr-1.5 h-4 w-4" />
      New playlist
    </a>
  </div>

  <div class="mt-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4" aria-label="Library overview">
    <div class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Videos</p>
      <p class="mt-3 text-3xl font-bold text-base-content">{overview?.inventory.totalMedia ?? totalCount}</p>
      <p class="mt-1 text-xs text-base-content/50">
        {overview ? `across ${overview.inventory.totalChannels} channels` : 'in your library'}
      </p>
    </div>
    <div class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Downloaded</p>
      <p class="mt-3 text-3xl font-bold text-base-content">{overview?.inventory.totalDownloads ?? '—'}</p>
      <p class="mt-1 text-xs text-base-content/50">
        {overview ? `${formatBytes(overview.inventory.totalBytes)} on disk` : 'download jobs'}
      </p>
    </div>
    <div class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Playlists</p>
      <p class="mt-3 text-3xl font-bold text-base-content">{overview?.inventory.totalPlaylists ?? '—'}</p>
      <p class="mt-1 text-xs text-base-content/50">on this server</p>
    </div>
    <div class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Watched</p>
      <p class="mt-3 text-3xl font-bold text-base-content">{overview?.watchProgress.watchedCount ?? '—'}</p>
      <p class="mt-1 text-xs text-base-content/50">
        {overview ? `${Math.round(overview.watchProgress.watchedPercent)}% of the library` : 'watch progress'}
      </p>
    </div>
  </div>

  <nav class="mt-7 flex gap-6 border-b border-base-300/70" aria-label="Library sections">
    {#each tabs as tab}
      <button
        type="button"
        onclick={() => selectTab(tab)}
        class={[
          '-mb-px border-b-2 pb-2.5 text-sm font-medium transition',
          activeTab === tab
            ? 'border-primary font-semibold text-base-content'
            : 'border-transparent text-base-content/50 hover:text-base-content/80'
        ]}
        aria-current={activeTab === tab ? 'page' : undefined}
      >
        {tab}
      </button>
    {/each}
  </nav>

  {#if activeTab === 'Videos' || activeTab === 'History' || activeTab === 'Liked'}
    <div class="mt-5 flex flex-wrap items-center justify-between gap-3">
      <p class="text-sm text-base-content/50">
        {gridSummary()}
      </p>
      {#if activeTab === 'Videos'}
        <Select items={sortOptions} bind:value={sort} onchange={changeSort} aria-label="Sort library" class="w-48 text-sm" />
      {/if}
    </div>

    {#if loadError}
      <div
        class="mt-6 flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
        role="alert"
      >
        <CircleAlert class="h-4 w-4 shrink-0" />
        <span>{loadError}</span>
        {#if needsLogin}
          <a class="btn btn-sm btn-primary ml-auto text-xs" href="/auth/login">
            Log in again
          </a>
        {/if}
      </div>
    {:else if loading}
      <div class="mt-16 flex justify-center">
        <span class="loading loading-spinner loading-md"></span>
      </div>
    {:else if items.length === 0}
      <div class="mt-10 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
        <LayoutList class="mx-auto h-10 w-10 text-base-content/30" />
        <p class="mt-4 text-sm font-semibold text-base-content/80">{emptyTitle()}</p>
        <p class="mt-1 text-sm text-base-content/50">{emptyDescription()}</p>
        {#if activeTab === 'Videos'}
          <a class="btn btn-sm btn-primary mt-5 text-xs" href="/download">
            Go to Download
          </a>
        {/if}
      </div>
    {:else}
      <div class="mt-5 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
        {#each items as card (card.mediaGuid)}
          <article class="group min-w-0">
            <a
              href={cardHref(card)}
              class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.mediaGuid)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
              aria-label={`Play ${card.title}`}
            >
              <span
                class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-3xl font-black text-white/15"
              >
                {initialsFor(card.account.accountName)}
              </span>
              {#if thumbnailUrl(card)}
                <img
                  src={thumbnailUrl(card)}
                  alt=""
                  loading="lazy"
                  decoding="async"
                  class="absolute inset-0 h-full w-full object-cover"
                  onerror={hideBrokenImage}
                />
              {/if}
              <span
                class="absolute left-3 top-3 rounded-md bg-black/50 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white/80"
              >
                {card.account.platform}
              </span>
              {#if formatDuration(card.durationSeconds)}
                <span
                  class="absolute bottom-3 right-3 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-semibold text-white"
                >
                  {formatDuration(card.durationSeconds)}
                </span>
              {/if}
              <span
                class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-900 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
              >
                <Play class="ml-0.5 h-5 w-5" />
              </span>
            </a>
            <div class="mt-3 flex min-w-0 gap-3 px-1">
              <a
                href={`/channel/${card.account.accountId}`}
                aria-label={`Open ${card.account.accountName}'s channel`}
                class={`relative mt-0.5 grid h-8 w-8 shrink-0 place-items-center overflow-hidden rounded-full bg-gradient-to-br ${accentFor(card.mediaGuid)} text-[10px] font-bold text-white`}
              >
                {initialsFor(card.account.accountName)}
                <img
                  src={`/api/media/watch/accounts/${card.account.accountId}/avatar`}
                  alt=""
                  loading="lazy"
                  decoding="async"
                  class="absolute inset-0 h-full w-full object-cover"
                  onerror={hideBrokenImage}
                />
              </a>
              <div class="min-w-0">
                <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-base-content/90">
                  {card.title}
                </h3>
                <p class="mt-1 truncate text-xs text-base-content/50">
                  <a href={`/channel/${card.account.accountId}`} class="hover:text-base-content/80">
                    {card.account.accountName}
                  </a>
                </p>
                <p class="mt-0.5 truncate text-xs text-base-content/40">
                  {cardMetaLine(card)}
                </p>
              </div>
            </div>
          </article>
        {/each}
      </div>

      <div class="mt-8 flex items-center justify-between border-t border-base-300/70 pt-5">
        <p class="text-xs text-base-content/40">
          Page {currentPage} of {totalPages}
        </p>
        <div class="flex gap-2">
          <button class="btn btn-sm btn-neutral text-xs" disabled={currentPage <= 1 || loading} onclick={() => loadPage(currentPage - 1)}>
            <ChevronLeft class="mr-1 h-3.5 w-3.5" />
            Previous
          </button>
          <button class="btn btn-sm btn-neutral text-xs" disabled={!hasMore || loading} onclick={() => loadPage(currentPage + 1)}>
            Next
            <ChevronRight class="ml-1 h-3.5 w-3.5" />
          </button>
        </div>
      </div>
    {/if}
  {:else if activeTab === 'Playlists'}
    <section class="mt-6" aria-labelledby="your-playlists-title">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h2 id="your-playlists-title" class="text-lg font-bold text-base-content">Your playlists</h2>
        <a
          href="/profile/playlists"
          class="text-xs font-semibold text-base-content/50 transition hover:text-base-content/80"
        >
          Manage in profile
        </a>
      </div>

      {#if userPlaylistsError}
        <div
          class="mt-4 flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
          role="alert"
        >
          <CircleAlert class="h-4 w-4 shrink-0" />
          <span>{userPlaylistsError}</span>
        </div>
      {:else if userPlaylistsLoading}
        <div class="mt-10 flex justify-center">
          <span class="loading loading-spinner loading-md"></span>
        </div>
      {:else if userPlaylistCards.length === 0}
        <div class="mt-4 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
          <ListMusic class="mx-auto h-10 w-10 text-base-content/30" />
          <p class="mt-4 text-sm font-semibold text-base-content/80">No playlists yet</p>
          <p class="mt-1 text-sm text-base-content/50">
            Create one from your profile, or save a video to a playlist from the watch page.
          </p>
        </div>
      {:else}
        <div class="mt-4 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
          {#each userPlaylistCards as card (card.playlist.playlistId)}
            <article class="group min-w-0">
              <a
                href={card.firstGuid
                  ? `/watch/${card.firstGuid}?ulist=${encodeURIComponent(card.playlist.playlistId)}`
                  : `/profile/playlists/${encodeURIComponent(card.playlist.playlistId)}`}
                class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.playlist.playlistId)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
                aria-label={card.firstGuid ? `Play playlist ${card.playlist.name}` : `Edit playlist ${card.playlist.name}`}
              >
                <ListMusic
                  class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/15"
                />
                {#if card.firstGuid}
                  <img
                    src={`/api/media/watch/${card.firstGuid}/thumbnail`}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    class="absolute inset-0 h-full w-full object-cover"
                    onerror={hideBrokenImage}
                  />
                {/if}
                <span
                  class="absolute bottom-0 left-0 right-0 flex items-center justify-between bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-white/90 backdrop-blur-sm"
                >
                  <span class="flex items-center gap-1.5">
                    <ListMusic class="h-3.5 w-3.5" />
                    Playlist
                  </span>
                  {card.playlist.itemCount} {card.playlist.itemCount === 1 ? 'video' : 'videos'}
                </span>
                {#if card.firstGuid}
                  <span
                    class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-900 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
                  >
                    <Play class="ml-0.5 h-5 w-5" />
                  </span>
                {/if}
              </a>
              <div class="mt-3 flex min-w-0 items-start justify-between gap-2 px-1">
                <div class="min-w-0">
                  <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-base-content/90">
                    {card.playlist.name}
                  </h3>
                  <p class="mt-1 truncate text-xs text-base-content/40">{userPlaylistMeta(card)}</p>
                </div>
                <a
                  href={`/profile/playlists/${encodeURIComponent(card.playlist.playlistId)}`}
                  class="mt-0.5 shrink-0 text-base-content/40 transition hover:text-base-content/80"
                  title="Edit playlist"
                  aria-label={`Edit playlist ${card.playlist.name}`}
                >
                  <Pen class="h-3.5 w-3.5" />
                </a>
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </section>

    <section class="mt-10" aria-labelledby="downloaded-playlists-title">
      <h2 id="downloaded-playlists-title" class="text-lg font-bold text-base-content">Downloaded playlists</h2>

      {#if platformPlaylistsError}
        <div
          class="mt-4 flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
          role="alert"
        >
          <CircleAlert class="h-4 w-4 shrink-0" />
          <span>{platformPlaylistsError}</span>
        </div>
      {:else if platformPlaylistsLoading}
        <div class="mt-10 flex justify-center">
          <span class="loading loading-spinner loading-md"></span>
        </div>
      {:else if platformPlaylistCards.length === 0}
        <div class="mt-4 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
          <ListMusic class="mx-auto h-10 w-10 text-base-content/30" />
          <p class="mt-4 text-sm font-semibold text-base-content/80">No downloaded playlists</p>
          <p class="mt-1 text-sm text-base-content/50">
            Queue a provider playlist from the Download page and it will show up here.
          </p>
        </div>
      {:else}
        <div class="mt-4 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
          {#each platformPlaylistCards as card (card.playlist.playlistId)}
            <article class="group min-w-0">
              {#if card.playlist.firstMediaGuid}
                <a
                  href={`/watch/${card.playlist.firstMediaGuid}?list=${encodeURIComponent(card.playlist.playlistId)}`}
                  class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.playlist.playlistId)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
                  aria-label={`Play playlist ${card.playlist.title ?? 'Untitled provider playlist'}`}
                >
                  <ListMusic
                    class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/15"
                  />
                  <img
                    src={`/api/media/watch/${card.playlist.firstMediaGuid}/thumbnail`}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    class="absolute inset-0 h-full w-full object-cover"
                    onerror={hideBrokenImage}
                  />
                  <span
                    class="absolute bottom-0 left-0 right-0 flex items-center justify-between bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-white/90 backdrop-blur-sm"
                  >
                    <span class="flex items-center gap-1.5">
                      <ListMusic class="h-3.5 w-3.5" />
                      Playlist
                    </span>
                    {card.playlist.itemCount} {card.playlist.itemCount === 1 ? 'video' : 'videos'}
                  </span>
                  <span
                    class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-900 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
                  >
                    <Play class="ml-0.5 h-5 w-5" />
                  </span>
                </a>
              {:else}
                <div
                  class="relative block aspect-video w-full overflow-hidden rounded-2xl bg-base-200/60 opacity-70"
                >
                  <ListMusic
                    class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/10"
                  />
                  <span
                    class="absolute bottom-0 left-0 right-0 bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-white/60"
                  >
                    Nothing downloaded yet
                  </span>
                </div>
              {/if}
              <div class="mt-3 min-w-0 px-1">
                <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-base-content/90">
                  {card.playlist.title ?? 'Untitled provider playlist'}
                </h3>
                <p class="mt-1 truncate text-xs text-base-content/40">{platformPlaylistMeta(card)}</p>
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </section>
  {:else}
    <div class="mt-10 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <p class="text-sm font-semibold text-base-content/80">{activeTab} is not wired up yet</p>
      <p class="mt-1 text-sm text-base-content/50">This section will arrive with a later iteration.</p>
    </div>
  {/if}
</section>
