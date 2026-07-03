<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Select, Spinner } from 'flowbite-svelte';
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    ExclamationCircleOutline,
    ListMusicOutline,
    PenOutline,
    PlaySolid,
    PlusOutline,
    RectangleListOutline
  } from 'flowbite-svelte-icons';
  import { getUserPlaylist, listUserPlaylists, type UserPlaylist } from '$lib/api/userPlaylists';
  import {
    getPlatformPlaylist,
    listPlatformPlaylists,
    type PlatformPlaylist
  } from '$lib/api/playlists';
  import {
    accentFor,
    formatBytes,
    formatDuration,
    formatRelativeDate,
    formatViews,
    initialsFor
  } from '$lib/media';

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
  const tabs = ['Videos', 'Playlists', 'Watch later', 'History', 'Liked'] as const;
  type Tab = (typeof tabs)[number];

  const sortOptions = [
    { value: 'release_date:desc', name: 'Recently added' },
    { value: 'release_date:asc', name: 'Oldest first' },
    { value: 'title:asc', name: 'Title A–Z' },
    { value: 'view_count:desc', name: 'Most viewed' },
    { value: 'duration:desc', name: 'Longest first' }
  ];

  interface UserPlaylistCard {
    playlist: UserPlaylist;
    firstGuid: string | null;
  }

  interface PlatformPlaylistCard {
    playlist: PlatformPlaylist;
    firstGuid: string | null;
  }

  let activeTab = $state<Tab>('Videos');
  let sort = $state('release_date:desc');
  let playlistsRequested = $state(false);
  let userPlaylistCards = $state<UserPlaylistCard[]>([]);
  let userPlaylistsLoading = $state(false);
  let userPlaylistsError = $state<string | null>(null);
  let platformPlaylistCards = $state<PlatformPlaylistCard[]>([]);
  let platformPlaylistsLoading = $state(false);
  let platformPlaylistsError = $state<string | null>(null);
  let page = $state(1);
  let items = $state<MediaCard[]>([]);
  let totalCount = $state(0);
  let hasMore = $state(false);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let needsLogin = $state(false);
  let overview = $state<Overview | null>(null);

  const totalPages = $derived(Math.max(1, Math.ceil(totalCount / pageSize)));

  onMount(() => {
    void loadPage(1);
    void loadOverview();
  });

  async function loadOverview() {
    try {
      const response = await fetch('/api/statistics/overview');
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
    const [sortBy, sortOrder] = sort.split(':');
    const query = new URLSearchParams({
      page: String(target),
      pageSize: String(pageSize),
      sortBy,
      sortOrder
    });

    try {
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
      page = data.page;
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
    activeTab = tab;
    if (tab === 'Playlists' && !playlistsRequested) {
      playlistsRequested = true;
      void loadUserPlaylistCards();
      void loadPlatformPlaylistCards();
    }
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
      const list = await listPlatformPlaylists();
      const details = await Promise.all(
        list.map((playlist) => getPlatformPlaylist(playlist.playlistId).catch(() => playlist))
      );
      platformPlaylistCards = details.map((playlist) => ({
        playlist,
        firstGuid:
          (playlist.items ?? [])
            .slice()
            .sort((a, b) => a.playlistIndex - b.playlistIndex)
            .find((item) => item.mediaGuid)?.mediaGuid ?? null
      }));
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
      `${playlist.completedItems} of ${playlist.totalItems} downloaded`,
      formatRelativeDate(playlist.updatedAt) ? `updated ${formatRelativeDate(playlist.updatedAt)}` : null
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function metaLine(card: MediaCard): string {
    return [formatViews(card.viewCount), card.wasLive ? 'was live' : null]
      .filter(Boolean)
      .join(' · ');
  }

  function thumbnailUrl(card: MediaCard): string | null {
    return card.thumbnailStoragePath ? `/stream/${card.mediaGuid}/thumbnail` : null;
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
      <h1 id="library-title" class="text-2xl font-bold tracking-tight text-white">Library</h1>
      <p class="mt-1 text-sm text-slate-500">Your playlists, saved videos, and files on this server.</p>
    </div>
    <Button
      href="/profile?section=Playlists"
      color="dark"
      class="border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800!"
    >
      <PlusOutline class="mr-1.5 h-4 w-4" />
      New playlist
    </Button>
  </div>

  <div class="mt-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4" aria-label="Library overview">
    <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Videos</p>
      <p class="mt-3 text-3xl font-bold text-white">{overview?.inventory.totalMedia ?? totalCount}</p>
      <p class="mt-1 text-xs text-slate-500">
        {overview ? `across ${overview.inventory.totalChannels} channels` : 'in your library'}
      </p>
    </div>
    <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Downloaded</p>
      <p class="mt-3 text-3xl font-bold text-white">{overview?.inventory.totalDownloads ?? '—'}</p>
      <p class="mt-1 text-xs text-slate-500">
        {overview ? `${formatBytes(overview.inventory.totalBytes)} on disk` : 'download jobs'}
      </p>
    </div>
    <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Playlists</p>
      <p class="mt-3 text-3xl font-bold text-white">{overview?.inventory.totalPlaylists ?? '—'}</p>
      <p class="mt-1 text-xs text-slate-500">on this server</p>
    </div>
    <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Watched</p>
      <p class="mt-3 text-3xl font-bold text-white">{overview?.watchProgress.watchedCount ?? '—'}</p>
      <p class="mt-1 text-xs text-slate-500">
        {overview ? `${Math.round(overview.watchProgress.watchedPercent)}% of the library` : 'watch progress'}
      </p>
    </div>
  </div>

  <nav class="mt-7 flex gap-6 border-b border-slate-800/70" aria-label="Library sections">
    {#each tabs as tab}
      <button
        type="button"
        onclick={() => selectTab(tab)}
        class={[
          '-mb-px border-b-2 pb-2.5 text-sm font-medium transition',
          activeTab === tab
            ? 'border-blue-500 font-semibold text-white'
            : 'border-transparent text-slate-500 hover:text-slate-300'
        ]}
        aria-current={activeTab === tab ? 'page' : undefined}
      >
        {tab}
      </button>
    {/each}
  </nav>

  {#if activeTab === 'Videos'}
    <div class="mt-5 flex flex-wrap items-center justify-between gap-3">
      <p class="text-sm text-slate-500">
        {loading ? 'Loading…' : `${totalCount} ${totalCount === 1 ? 'title' : 'titles'} on the server`}
      </p>
      <Select
        items={sortOptions}
        bind:value={sort}
        onchange={changeSort}
        aria-label="Sort library"
        class="w-48! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>

    {#if loadError}
      <div
        class="mt-6 flex items-center gap-3 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
        <span>{loadError}</span>
        {#if needsLogin}
          <Button
            href="/auth/login"
            color="blue"
            class="ml-auto border-0! bg-blue-500! px-4! py-1.5! text-xs! font-semibold! hover:bg-blue-400!"
          >
            Log in again
          </Button>
        {/if}
      </div>
    {:else if loading}
      <div class="mt-16 flex justify-center">
        <Spinner size="8" />
      </div>
    {:else if items.length === 0}
      <div class="mt-10 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
        <RectangleListOutline class="mx-auto h-10 w-10 text-slate-700" />
        <p class="mt-4 text-sm font-semibold text-slate-300">No titles yet</p>
        <p class="mt-1 text-sm text-slate-500">
          Queue something from the Download page and it will show up here once processed.
        </p>
        <Button
          href="/download"
          color="blue"
          class="mt-5 border-0! bg-blue-500! px-5! py-2! text-xs! font-semibold! hover:bg-blue-400!"
        >
          Go to Download
        </Button>
      </div>
    {:else}
      <div class="mt-5 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
        {#each items as card (card.mediaGuid)}
          <article class="group min-w-0">
            <a
              href={`/watch/${card.mediaGuid}`}
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
                class="absolute left-3 top-3 rounded-md bg-black/50 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-slate-300"
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
                class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-950 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
              >
                <PlaySolid class="ml-0.5 h-5 w-5" />
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
                  src={`/stream/accounts/${card.account.accountId}/avatar`}
                  alt=""
                  loading="lazy"
                  decoding="async"
                  class="absolute inset-0 h-full w-full object-cover"
                  onerror={hideBrokenImage}
                />
              </a>
              <div class="min-w-0">
                <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-slate-200">
                  {card.title}
                </h3>
                <p class="mt-1 truncate text-xs text-slate-500">
                  <a href={`/channel/${card.account.accountId}`} class="hover:text-slate-300">
                    {card.account.accountName}
                  </a>
                </p>
                <p class="mt-0.5 truncate text-xs text-slate-600">
                  {[metaLine(card), formatRelativeDate(card.releaseDate)].filter(Boolean).join(' · ')}
                </p>
              </div>
            </div>
          </article>
        {/each}
      </div>

      <div class="mt-8 flex items-center justify-between border-t border-slate-800/70 pt-5">
        <p class="text-xs text-slate-600">
          Page {page} of {totalPages}
        </p>
        <div class="flex gap-2">
          <Button
            color="dark"
            disabled={page <= 1 || loading}
            onclick={() => loadPage(page - 1)}
            class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
          >
            <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
            Previous
          </Button>
          <Button
            color="dark"
            disabled={!hasMore || loading}
            onclick={() => loadPage(page + 1)}
            class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
          >
            Next
            <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
          </Button>
        </div>
      </div>
    {/if}
  {:else if activeTab === 'Playlists'}
    <section class="mt-6" aria-labelledby="your-playlists-title">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h2 id="your-playlists-title" class="text-lg font-bold text-white">Your playlists</h2>
        <a
          href="/profile?section=Playlists"
          class="text-xs font-semibold text-slate-500 transition hover:text-slate-300"
        >
          Manage in profile
        </a>
      </div>

      {#if userPlaylistsError}
        <div
          class="mt-4 flex items-center gap-3 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
          role="alert"
        >
          <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
          <span>{userPlaylistsError}</span>
        </div>
      {:else if userPlaylistsLoading}
        <div class="mt-10 flex justify-center">
          <Spinner size="8" />
        </div>
      {:else if userPlaylistCards.length === 0}
        <div class="mt-4 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
          <ListMusicOutline class="mx-auto h-10 w-10 text-slate-700" />
          <p class="mt-4 text-sm font-semibold text-slate-300">No playlists yet</p>
          <p class="mt-1 text-sm text-slate-500">
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
                <ListMusicOutline
                  class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/15"
                />
                {#if card.firstGuid}
                  <img
                    src={`/stream/${card.firstGuid}/thumbnail`}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    class="absolute inset-0 h-full w-full object-cover"
                    onerror={hideBrokenImage}
                  />
                {/if}
                <span
                  class="absolute bottom-0 left-0 right-0 flex items-center justify-between bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-slate-200 backdrop-blur-sm"
                >
                  <span class="flex items-center gap-1.5">
                    <ListMusicOutline class="h-3.5 w-3.5" />
                    Playlist
                  </span>
                  {card.playlist.itemCount} {card.playlist.itemCount === 1 ? 'video' : 'videos'}
                </span>
                {#if card.firstGuid}
                  <span
                    class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-950 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
                  >
                    <PlaySolid class="ml-0.5 h-5 w-5" />
                  </span>
                {/if}
              </a>
              <div class="mt-3 flex min-w-0 items-start justify-between gap-2 px-1">
                <div class="min-w-0">
                  <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-slate-200">
                    {card.playlist.name}
                  </h3>
                  <p class="mt-1 truncate text-xs text-slate-600">{userPlaylistMeta(card)}</p>
                </div>
                <a
                  href={`/profile/playlists/${encodeURIComponent(card.playlist.playlistId)}`}
                  class="mt-0.5 shrink-0 text-slate-600 transition hover:text-slate-300"
                  title="Edit playlist"
                  aria-label={`Edit playlist ${card.playlist.name}`}
                >
                  <PenOutline class="h-3.5 w-3.5" />
                </a>
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </section>

    <section class="mt-10" aria-labelledby="downloaded-playlists-title">
      <h2 id="downloaded-playlists-title" class="text-lg font-bold text-white">Downloaded playlists</h2>

      {#if platformPlaylistsError}
        <div
          class="mt-4 flex items-center gap-3 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
          role="alert"
        >
          <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
          <span>{platformPlaylistsError}</span>
        </div>
      {:else if platformPlaylistsLoading}
        <div class="mt-10 flex justify-center">
          <Spinner size="8" />
        </div>
      {:else if platformPlaylistCards.length === 0}
        <div class="mt-4 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
          <ListMusicOutline class="mx-auto h-10 w-10 text-slate-700" />
          <p class="mt-4 text-sm font-semibold text-slate-300">No downloaded playlists</p>
          <p class="mt-1 text-sm text-slate-500">
            Queue a provider playlist from the Download page and it will show up here.
          </p>
        </div>
      {:else}
        <div class="mt-4 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
          {#each platformPlaylistCards as card (card.playlist.playlistId)}
            <article class="group min-w-0">
              {#if card.firstGuid}
                <a
                  href={`/watch/${card.firstGuid}?list=${encodeURIComponent(card.playlist.playlistId)}`}
                  class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.playlist.playlistId)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
                  aria-label={`Play playlist ${card.playlist.title ?? card.playlist.sourceUrl}`}
                >
                  <ListMusicOutline
                    class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/15"
                  />
                  <img
                    src={`/stream/${card.firstGuid}/thumbnail`}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    class="absolute inset-0 h-full w-full object-cover"
                    onerror={hideBrokenImage}
                  />
                  <span
                    class="absolute bottom-0 left-0 right-0 flex items-center justify-between bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-slate-200 backdrop-blur-sm"
                  >
                    <span class="flex items-center gap-1.5">
                      <ListMusicOutline class="h-3.5 w-3.5" />
                      Playlist
                    </span>
                    {card.playlist.completedItems} / {card.playlist.totalItems}
                  </span>
                  <span
                    class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-950 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
                  >
                    <PlaySolid class="ml-0.5 h-5 w-5" />
                  </span>
                </a>
              {:else}
                <div
                  class="relative block aspect-video w-full overflow-hidden rounded-2xl bg-slate-900/60 opacity-70"
                >
                  <ListMusicOutline
                    class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/10"
                  />
                  <span
                    class="absolute bottom-0 left-0 right-0 bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-slate-400"
                  >
                    Nothing downloaded yet
                  </span>
                </div>
              {/if}
              <div class="mt-3 min-w-0 px-1">
                <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-slate-200">
                  {card.playlist.title ?? card.playlist.sourceUrl}
                </h3>
                <p class="mt-1 truncate text-xs text-slate-600">{platformPlaylistMeta(card)}</p>
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </section>
  {:else}
    <div class="mt-10 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <p class="text-sm font-semibold text-slate-300">{activeTab} is not wired up yet</p>
      <p class="mt-1 text-sm text-slate-500">This section will arrive with a later iteration.</p>
    </div>
  {/if}
</section>
