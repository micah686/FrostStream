<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Select, Spinner } from 'flowbite-svelte';
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    ExclamationCircleOutline,
    PlaySolid,
    PlusOutline,
    RectangleListOutline
  } from 'flowbite-svelte-icons';

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

  let activeTab = $state<Tab>('Videos');
  let sort = $state('release_date:desc');
  let page = $state(1);
  let items = $state<MediaCard[]>([]);
  let totalCount = $state(0);
  let hasMore = $state(false);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
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

  // Deterministic placeholder art until the thumbnail asset endpoint exists.
  const accents = [
    'from-slate-800 to-blue-950',
    'from-purple-950 to-violet-700',
    'from-red-950 to-orange-900',
    'from-blue-950 to-slate-800',
    'from-emerald-950 to-teal-800',
    'from-fuchsia-950 to-pink-800'
  ];

  function hashOf(value: string): number {
    let hash = 0;
    for (let i = 0; i < value.length; i += 1) {
      hash = (hash * 31 + value.charCodeAt(i)) | 0;
    }
    return Math.abs(hash);
  }

  function accentFor(card: MediaCard): string {
    return accents[hashOf(card.mediaGuid) % accents.length];
  }

  function initialsFor(name: string): string {
    const words = name.trim().split(/\s+/).filter(Boolean);
    if (words.length === 0) {
      return '?';
    }
    return (words[0][0] + (words.length > 1 ? words[words.length - 1][0] : '')).toUpperCase();
  }

  function formatDuration(seconds: number | null | undefined): string | null {
    if (!seconds || seconds <= 0) {
      return null;
    }
    const total = Math.round(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    return h > 0
      ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
      : `${m}:${String(s).padStart(2, '0')}`;
  }

  function formatViews(count: number | null | undefined): string | null {
    if (count === null || count === undefined) {
      return null;
    }
    if (count >= 1_000_000) {
      return `${(count / 1_000_000).toFixed(1)}M views`;
    }
    if (count >= 1_000) {
      return `${(count / 1_000).toFixed(1)}K views`;
    }
    return `${count} views`;
  }

  function formatDate(iso: string | null | undefined): string | null {
    if (!iso) {
      return null;
    }
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    const days = Math.floor((Date.now() - date.getTime()) / 86_400_000);
    if (days <= 0) {
      return 'Today';
    }
    if (days === 1) {
      return 'Yesterday';
    }
    if (days < 30) {
      return `${days} days ago`;
    }
    return date.toLocaleDateString();
  }

  function formatBytes(bytes: number): string {
    if (bytes >= 1_099_511_627_776) {
      return `${(bytes / 1_099_511_627_776).toFixed(1)} TB`;
    }
    if (bytes >= 1_073_741_824) {
      return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
    }
    if (bytes >= 1_048_576) {
      return `${(bytes / 1_048_576).toFixed(0)} MB`;
    }
    return `${bytes} B`;
  }

  function formatHours(seconds: number): string {
    const hours = seconds / 3600;
    return hours >= 1 ? `${Math.round(hours)}h` : `${Math.round(seconds / 60)}m`;
  }

  function metaLine(card: MediaCard): string {
    return [formatViews(card.viewCount), card.wasLive ? 'was live' : null]
      .filter(Boolean)
      .join(' · ');
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
        onclick={() => (activeTab = tab)}
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
        class="mt-6 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{loadError}</span>
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
            <button
              type="button"
              class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
              aria-label={`Play ${card.title}`}
            >
              <span
                class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-3xl font-black text-white/15"
              >
                {initialsFor(card.account.accountName)}
              </span>
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
            </button>
            <div class="mt-3 flex min-w-0 gap-3 px-1">
              <span
                class={`mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-full bg-gradient-to-br ${accentFor(card)} text-[10px] font-bold text-white`}
              >
                {initialsFor(card.account.accountName)}
              </span>
              <div class="min-w-0">
                <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-slate-200">
                  {card.title}
                </h3>
                <p class="mt-1 truncate text-xs text-slate-500">{card.account.accountName}</p>
                <p class="mt-0.5 truncate text-xs text-slate-600">
                  {[metaLine(card), formatDate(card.releaseDate)].filter(Boolean).join(' · ')}
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
  {:else}
    <div class="mt-10 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <p class="text-sm font-semibold text-slate-300">{activeTab} is not wired up yet</p>
      <p class="mt-1 text-sm text-slate-500">This section will arrive with a later iteration.</p>
    </div>
  {/if}
</section>
