<script lang="ts">
  import { page } from '$app/state';
  import { Button, Select, Spinner } from 'flowbite-svelte';
  import {
    ArrowUpRightFromSquareOutline,
    ChevronDownOutline,
    ChevronLeftOutline,
    ChevronRightOutline,
    ChevronUpOutline,
    ExclamationCircleOutline,
    PlaySolid,
    RectangleListOutline
  } from 'flowbite-svelte-icons';
  import { accentFor, formatCount, formatDuration, formatRelativeDate, formatViews, initialsFor } from '$lib/media';

  interface Account {
    accountId: number;
    platform: string;
    accountName: string;
    accountHandle: string;
    accountUrl?: string | null;
    accountCreationDate?: string | null;
    followerCount?: number | null;
    isVerified: boolean;
    description?: string | null;
    avatarStoragePath?: string | null;
    bannerStoragePath?: string | null;
    mediaCount: number;
    userNote?: string | null;
  }

  interface MediaCard {
    mediaGuid: string;
    title: string;
    thumbnailStoragePath?: string | null;
    durationSeconds?: number | null;
    releaseDate?: string | null;
    viewCount?: number | null;
    wasLive: boolean;
  }

  interface PagedResponse {
    items: MediaCard[];
    page: number;
    totalCount: number;
    hasMore: boolean;
  }

  const pageSize = 24;

  const sortOptions = [
    { value: 'release_date:desc', name: 'Newest first' },
    { value: 'release_date:asc', name: 'Oldest first' },
    { value: 'title:asc', name: 'Title A–Z' },
    { value: 'view_count:desc', name: 'Most viewed' },
    { value: 'duration:desc', name: 'Longest first' }
  ];

  let account = $state<Account | null>(null);
  let loadError = $state<string | null>(null);
  let descriptionExpanded = $state(false);
  let bannerBroken = $state(false);
  let avatarBroken = $state(false);

  let sort = $state('release_date:desc');
  let mediaPage = $state(1);
  let items = $state<MediaCard[]>([]);
  let totalCount = $state(0);
  let hasMore = $state(false);
  let mediaLoading = $state(true);
  let mediaError = $state<string | null>(null);

  const accountId = $derived(page.params.accountId ?? '');
  const totalPages = $derived(Math.max(1, Math.ceil(totalCount / pageSize)));
  const bannerUrl = $derived(
    account?.bannerStoragePath && !bannerBroken ? `/stream/accounts/${account.accountId}/banner` : null
  );
  const avatarUrl = $derived(
    account?.avatarStoragePath && !avatarBroken ? `/stream/accounts/${account.accountId}/avatar` : null
  );

  $effect(() => {
    if (accountId) {
      void loadAll(accountId);
    }
  });

  async function loadAll(id: string) {
    account = null;
    loadError = null;
    descriptionExpanded = false;
    bannerBroken = false;
    avatarBroken = false;
    sort = 'release_date:desc';
    items = [];
    totalCount = 0;
    hasMore = false;

    await Promise.all([loadAccount(id), loadMedia(id, 1)]);
  }

  async function loadAccount(id: string) {
    try {
      const response = await fetch(`/api/metadata/accounts/${id}`);
      if (!response.ok) {
        loadError =
          response.status === 401
            ? 'Your session has expired — log in again from the button in the top bar.'
            : response.status === 404
              ? 'This creator does not exist on the server.'
              : `Could not load the creator (status ${response.status}).`;
        return;
      }
      account = (await response.json()) as Account;
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the creator.';
    }
  }

  async function loadMedia(id: string, target: number) {
    mediaLoading = true;
    mediaError = null;
    const [sortBy, sortOrder] = sort.split(':');
    const query = new URLSearchParams({
      page: String(target),
      pageSize: String(pageSize),
      sortBy,
      sortOrder
    });

    try {
      const response = await fetch(`/api/metadata/accounts/${id}/media?${query}`);
      if (!response.ok) {
        mediaError = `Could not load this creator's videos (status ${response.status}).`;
        return;
      }
      const data = (await response.json()) as PagedResponse;
      items = data.items;
      mediaPage = data.page;
      totalCount = data.totalCount;
      hasMore = data.hasMore;
    } catch (err) {
      mediaError = err instanceof Error ? err.message : "Could not load this creator's videos.";
    } finally {
      mediaLoading = false;
    }
  }

  function changeSort() {
    void loadMedia(accountId, 1);
  }

  function joinedDate(iso: string | null | undefined): string | null {
    if (!iso) {
      return null;
    }
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    return date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  function statLine(current: Account): string {
    return [
      formatCount(current.followerCount) ? `${formatCount(current.followerCount)} subscribers` : null,
      `${current.mediaCount.toLocaleString()} ${current.mediaCount === 1 ? 'video' : 'videos'} archived`,
      joinedDate(current.accountCreationDate) ? `joined ${joinedDate(current.accountCreationDate)}` : null
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function cardMeta(card: MediaCard): string {
    return [formatViews(card.viewCount), card.wasLive ? 'was live' : null, formatRelativeDate(card.releaseDate)]
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
  <title>{account ? `${account.accountName} · FrostStream` : 'Channel · FrostStream'}</title>
</svelte:head>

{#if loadError}
  <div
    class="flex items-center gap-3 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
    role="alert"
  >
    <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
    <span>{loadError}</span>
  </div>
{:else if !account}
  <div class="mt-16 flex justify-center">
    <Spinner size="8" />
  </div>
{:else}
  <section aria-labelledby="channel-title">
    <div
      class={`relative h-36 w-full overflow-hidden rounded-2xl bg-gradient-to-br sm:h-48 ${accentFor(account.accountName)} shadow-lg shadow-black/20`}
    >
      {#if bannerUrl}
        <img
          src={bannerUrl}
          alt=""
          decoding="async"
          class="absolute inset-0 h-full w-full object-cover"
          onerror={() => (bannerBroken = true)}
        />
      {:else}
        <span
          class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-6xl font-black text-white/10"
        >
          {initialsFor(account.accountName)}
        </span>
      {/if}
    </div>

    <div class="mt-4 flex flex-col gap-5 px-1 sm:mt-5 sm:flex-row sm:items-start sm:gap-6">
      <span
        class={`relative -mt-14 grid h-28 w-28 shrink-0 place-items-center overflow-hidden rounded-full border-4 border-slate-950 bg-gradient-to-br text-3xl font-bold text-white sm:-mt-16 sm:h-36 sm:w-36 sm:text-4xl ${accentFor(account.accountName)} shadow-xl shadow-black/30`}
      >
        {initialsFor(account.accountName)}
        {#if avatarUrl}
          <img
            src={avatarUrl}
            alt={`${account.accountName} avatar`}
            decoding="async"
            class="absolute inset-0 h-full w-full object-cover"
            onerror={() => (avatarBroken = true)}
          />
        {/if}
      </span>

      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <h1 id="channel-title" class="text-2xl font-bold tracking-tight text-white sm:text-3xl">
            {account.accountName}
          </h1>
          {#if account.isVerified}
            <span class="text-lg text-blue-400" title="Verified">✓</span>
          {/if}
          <span
            class="rounded-md bg-slate-800/80 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-slate-400"
          >
            {account.platform}
          </span>
        </div>
        <p class="mt-1 text-sm text-slate-500">@{account.accountHandle}</p>
        <p class="mt-1.5 text-sm text-slate-400">{statLine(account)}</p>

        {#if account.description}
          <p
            class={[
              'mt-3 max-w-3xl whitespace-pre-line text-sm leading-6 text-slate-400',
              !descriptionExpanded && 'line-clamp-2'
            ]}
          >
            {account.description}
          </p>
          <button
            type="button"
            onclick={() => (descriptionExpanded = !descriptionExpanded)}
            class="mt-1.5 flex items-center gap-1 text-xs font-semibold text-slate-500 transition hover:text-slate-300"
          >
            {descriptionExpanded ? 'Show less' : 'Show more'}
            {#if descriptionExpanded}
              <ChevronUpOutline class="h-3 w-3" />
            {:else}
              <ChevronDownOutline class="h-3 w-3" />
            {/if}
          </button>
        {/if}

        {#if account.userNote}
          <div class="mt-3 max-w-3xl rounded-xl border border-slate-800/80 bg-slate-900/40 px-4 py-3">
            <h2 class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Your note</h2>
            <p class="mt-1 whitespace-pre-line text-sm text-slate-300">{account.userNote}</p>
          </div>
        {/if}
      </div>

      {#if account.accountUrl}
        <Button
          href={account.accountUrl}
          target="_blank"
          rel="noopener noreferrer"
          color="dark"
          class="shrink-0 border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800!"
        >
          <ArrowUpRightFromSquareOutline class="mr-1.5 h-3.5 w-3.5" />
          View on {account.platform}
        </Button>
      {/if}
    </div>

    <div class="mt-8 flex flex-wrap items-center justify-between gap-3 border-t border-slate-800/70 pt-5">
      <p class="text-sm text-slate-500">
        {mediaLoading ? 'Loading…' : `${totalCount} ${totalCount === 1 ? 'video' : 'videos'} on the server`}
      </p>
      <Select
        items={sortOptions}
        bind:value={sort}
        onchange={changeSort}
        aria-label="Sort videos"
        class="w-48! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>

    {#if mediaError}
      <div
        class="mt-6 flex items-center gap-3 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
        <span>{mediaError}</span>
      </div>
    {:else if mediaLoading}
      <div class="mt-16 flex justify-center">
        <Spinner size="8" />
      </div>
    {:else if items.length === 0}
      <div class="mt-10 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
        <RectangleListOutline class="mx-auto h-10 w-10 text-slate-700" />
        <p class="mt-4 text-sm font-semibold text-slate-300">No videos archived yet</p>
        <p class="mt-1 text-sm text-slate-500">
          Nothing from this creator has been downloaded to the server so far.
        </p>
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
                {initialsFor(account.accountName)}
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
            <div class="mt-3 min-w-0 px-1">
              <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-slate-200">
                {card.title}
              </h3>
              {#if cardMeta(card)}
                <p class="mt-1 truncate text-xs text-slate-600">{cardMeta(card)}</p>
              {/if}
            </div>
          </article>
        {/each}
      </div>

      <div class="mt-8 flex items-center justify-between border-t border-slate-800/70 pt-5">
        <p class="text-xs text-slate-600">
          Page {mediaPage} of {totalPages}
        </p>
        <div class="flex gap-2">
          <Button
            color="dark"
            disabled={mediaPage <= 1 || mediaLoading}
            onclick={() => loadMedia(accountId, mediaPage - 1)}
            class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
          >
            <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
            Previous
          </Button>
          <Button
            color="dark"
            disabled={!hasMore || mediaLoading}
            onclick={() => loadMedia(accountId, mediaPage + 1)}
            class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
          >
            Next
            <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
          </Button>
        </div>
      </div>
    {/if}
  </section>
{/if}
