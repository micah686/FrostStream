<script lang="ts">
  import { goto } from '$app/navigation';
  import { page as pageState } from '$app/state';
  import { Select } from '$lib/components/ui';
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    ExclamationCircleOutline,
    PlaySolid,
    SearchOutline
  } from 'flowbite-svelte-icons';
  import { accentFor, formatDuration, formatRelativeDate, formatViews, initialsFor } from '$lib/media';
  import { findSimilarMedia, searchMedia, searchMatchLabel, type SearchHit, type SearchScope } from '$lib/api/search';

  const pageSize = 24;

  const scopes: { value: SearchScope; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'metadata', label: 'Metadata' },
    { value: 'subtitles', label: 'Subtitles' },
    { value: 'comments', label: 'Comments' }
  ];

  const sortOptions = [
    { value: 'relevance', name: 'Most relevant' },
    { value: 'release_date:desc', name: 'Recently added' },
    { value: 'release_date:asc', name: 'Oldest first' },
    { value: 'title:asc', name: 'Title A–Z' },
    { value: 'view_count:desc', name: 'Most viewed' },
    { value: 'duration:desc', name: 'Longest first' }
  ];

  const query = $derived(pageState.url.searchParams.get('q')?.trim() ?? '');
  // Similar mode: ?similar=<mediaGuid> shows more-like-this results instead of a text search.
  const similarGuid = $derived(pageState.url.searchParams.get('similar')?.trim() ?? '');
  const scope = $derived((pageState.url.searchParams.get('scope') as SearchScope) || 'all');
  const currentPage = $derived(Math.max(1, Number(pageState.url.searchParams.get('page')) || 1));
  const sort = $derived(pageState.url.searchParams.get('sort') || 'relevance');

  let hits = $state<SearchHit[]>([]);
  let totalCount = $state(0);
  let hasMore = $state(false);
  let loading = $state(false);
  let loadError = $state<string | null>(null);
  let similarSourceTitle = $state<string | null>(null);

  const totalPages = $derived(Math.max(1, Math.ceil(totalCount / pageSize)));

  let requestId = 0;

  $effect(() => {
    const similar = similarGuid;
    similarSourceTitle = null;
    if (!similar) {
      return;
    }
    // Names the header ("Similar to …"); the results don't depend on it.
    fetch(`/api/metadata/${similar}`)
      .then((response) => (response.ok ? response.json() : null))
      .then((detail: { title?: string } | null) => {
        if (detail?.title && similarGuid === similar) {
          similarSourceTitle = detail.title;
        }
      })
      .catch(() => {});
  });

  $effect(() => {
    // Re-run whenever the URL-derived inputs change.
    const q = query;
    const similar = similarGuid;
    const currentScope = scope;
    const target = currentPage;
    const currentSort = sort;

    if (similar) {
      const id = ++requestId;
      loading = true;
      loadError = null;
      findSimilarMedia(similar, 30)
        .then((items) => {
          if (id !== requestId) {
            return;
          }
          hits = items;
          totalCount = items.length;
          hasMore = false;
        })
        .catch((err: unknown) => {
          if (id !== requestId) {
            return;
          }
          loadError = err instanceof Error ? err.message : 'Similar-media lookup failed.';
          hits = [];
          totalCount = 0;
          hasMore = false;
        })
        .finally(() => {
          if (id === requestId) {
            loading = false;
          }
        });
      return;
    }

    if (!q) {
      hits = [];
      totalCount = 0;
      hasMore = false;
      loadError = null;
      return;
    }

    const id = ++requestId;
    loading = true;
    loadError = null;

    const [sortBy, sortOrder] = currentSort === 'relevance' ? [undefined, undefined] : currentSort.split(':');
    searchMedia(q, {
      scope: currentScope,
      page: target,
      pageSize,
      sortBy,
      sortOrder: sortOrder as 'asc' | 'desc' | undefined
    })
      .then((result) => {
        if (id !== requestId) {
          return;
        }
        hits = result.items;
        totalCount = result.totalCount;
        hasMore = result.hasMore;
      })
      .catch((err: unknown) => {
        if (id !== requestId) {
          return;
        }
        loadError = err instanceof Error ? err.message : 'Search failed.';
        hits = [];
        totalCount = 0;
        hasMore = false;
      })
      .finally(() => {
        if (id === requestId) {
          loading = false;
        }
      });
  });

  function navigate(params: { scope?: SearchScope; page?: number; sort?: string }) {
    const url = new URL(pageState.url);
    url.searchParams.set('q', query);
    url.searchParams.set('scope', params.scope ?? scope);
    url.searchParams.set('sort', params.sort ?? sort);
    url.searchParams.set('page', String(params.page ?? 1));
    void goto(`${url.pathname}${url.search}`, { keepFocus: true, noScroll: params.page === undefined });
  }

  function metaLine(hit: SearchHit): string {
    return [
      formatViews(hit.media.viewCount),
      hit.media.wasLive ? 'was live' : null,
      formatRelativeDate(hit.media.releaseDate)
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function thumbnailUrl(hit: SearchHit): string | null {
    return hit.media.thumbnailStoragePath ? `/api/media/watch/${hit.media.mediaGuid}/thumbnail` : null;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<svelte:head>
  <title>{similarGuid ? 'Similar videos · Search' : query ? `${query} · Search` : 'Search'} · FrostStream</title>
</svelte:head>

<section aria-labelledby="search-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div class="min-w-0">
      <h1 id="search-title" class="text-2xl font-bold tracking-tight text-base-content">
        {similarGuid ? 'Similar videos' : query ? `Results for "${query}"` : 'Search'}
      </h1>
      <p class="mt-1 text-sm text-base-content/50">
        {#if similarGuid}
          {#if loading}
            Finding similar videos…
          {:else}
            {totalCount} {totalCount === 1 ? 'video' : 'videos'} similar to
            {#if similarSourceTitle}
              <a href={`/watch/${similarGuid}`} class="text-base-content/60 hover:text-base-content/90 hover:underline">
                "{similarSourceTitle}"
              </a>
            {:else}
              this video
            {/if}
          {/if}
        {:else if !query}
          Type in the search box above. Advanced syntax is supported, e.g.
          <code class="rounded bg-base-300/80 px-1.5 py-0.5 font-mono text-xs text-base-content/60">channel:LinusTechTips codec:h264 after:2023 duration:&gt;600</code>
          — or use the
          <a href="/search/advanced" class="text-primary hover:text-primary hover:underline">advanced search builder</a>.
        {:else if loading}
          Searching…
        {:else}
          {totalCount} {totalCount === 1 ? 'result' : 'results'}
        {/if}
      </p>
    </div>
    {#if query}
      <div class="flex shrink-0 items-center gap-3">
        <a
          href={`/search/advanced?q=${encodeURIComponent(query)}`}
          class="text-xs font-medium text-base-content/60 transition hover:text-base-content/90 hover:underline"
        >
          Advanced
        </a>
        <Select items={sortOptions} value={sort} onchange={(event) => navigate({ sort: (event.currentTarget as HTMLSelectElement).value })} aria-label="Sort results" class="w-48 text-sm" />
      </div>
    {/if}
  </div>

  {#if query}
    <nav class="mt-6 flex gap-6 border-b border-base-300/70" aria-label="Search scope">
      {#each scopes as item}
        <button
          type="button"
          onclick={() => navigate({ scope: item.value })}
          class={[
            '-mb-px border-b-2 pb-2.5 text-sm font-medium transition',
            scope === item.value
              ? 'border-primary font-semibold text-base-content'
              : 'border-transparent text-base-content/50 hover:text-base-content/80'
          ]}
          aria-current={scope === item.value ? 'page' : undefined}
        >
          {item.label}
        </button>
      {/each}
    </nav>
  {/if}

  {#if loadError}
    <div
      class="mt-6 flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
      role="alert"
    >
      <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {:else if loading}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if similarGuid && hits.length === 0}
    <div class="mt-10 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <SearchOutline class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No similar videos found</p>
      <p class="mt-1 text-sm text-base-content/50">
        Nothing else on the server is close enough to this video yet.
      </p>
    </div>
  {:else if query && hits.length === 0}
    <div class="mt-10 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
      <SearchOutline class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No results for "{query}"</p>
      <p class="mt-1 text-sm text-base-content/50">
        Try different keywords, a broader scope, or advanced filters like
        <code class="rounded bg-base-300/80 px-1.5 py-0.5 font-mono text-xs">channel:</code>,
        <code class="rounded bg-base-300/80 px-1.5 py-0.5 font-mono text-xs">resolution:</code>, or
        <code class="rounded bg-base-300/80 px-1.5 py-0.5 font-mono text-xs">after:</code>.
      </p>
    </div>
  {:else if hits.length > 0}
    <div class="mt-6 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
      {#each hits as hit (hit.media.mediaGuid)}
        <article class="group min-w-0">
          <a
            href={`/watch/${hit.media.mediaGuid}`}
            class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(hit.media.mediaGuid)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
            aria-label={`Play ${hit.media.title}`}
          >
            <span class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-3xl font-black text-white/15">
              {initialsFor(hit.media.account.accountName)}
            </span>
            {#if thumbnailUrl(hit)}
              <img
                src={thumbnailUrl(hit)}
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
              {hit.media.account.platform}
            </span>
            {#if formatDuration(hit.media.durationSeconds)}
              <span
                class="absolute bottom-3 right-3 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-semibold text-white"
              >
                {formatDuration(hit.media.durationSeconds)}
              </span>
            {/if}
            <span
              class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-900 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
            >
              <PlaySolid class="ml-0.5 h-5 w-5" />
            </span>
          </a>
          <div class="mt-3 flex min-w-0 gap-3 px-1">
            <span
              class={`mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-full bg-gradient-to-br ${accentFor(hit.media.mediaGuid)} text-[10px] font-bold text-white`}
            >
              {initialsFor(hit.media.account.accountName)}
            </span>
            <div class="min-w-0">
              <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-base-content/90">
                {hit.media.title}
              </h3>
              <p class="mt-1 truncate text-xs text-base-content/50">{hit.media.account.accountName}</p>
              {#if metaLine(hit)}
                <p class="mt-0.5 truncate text-xs text-base-content/40">{metaLine(hit)}</p>
              {/if}
              {#if hit.matchedIn.length > 0 && !(hit.matchedIn.length === 1 && hit.matchedIn[0] === 'metadata')}
                <div class="mt-1.5 flex flex-wrap gap-1">
                  {#each hit.matchedIn as match (match)}
                    <span class="rounded-full bg-primary/12 px-2 py-0.5 text-[10px] font-semibold text-primary">
                      {searchMatchLabel(match)}
                    </span>
                  {/each}
                </div>
              {/if}
            </div>
          </div>
        </article>
      {/each}
    </div>

    {#if !similarGuid}
    <div class="mt-8 flex items-center justify-between border-t border-base-300/70 pt-5">
      <p class="text-xs text-base-content/40">
        Page {currentPage} of {totalPages}
      </p>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-neutral text-xs" disabled={currentPage <= 1 || loading} onclick={() => navigate({ page: currentPage - 1 })}>
          <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
          Previous
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={!hasMore || loading} onclick={() => navigate({ page: currentPage + 1 })}>
          Next
          <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
        </button>
      </div>
    </div>
    {/if}
  {/if}
</section>
