<script lang="ts">
  import { page } from '$app/state';
  import { Button, Spinner } from 'flowbite-svelte';
  import {
    ChevronDownOutline,
    ChevronUpOutline,
    DotsHorizontalOutline,
    ExclamationCircleOutline,
    FolderOutline,
    PlusOutline,
    ShareNodesOutline,
    ThumbsDownOutline,
    ThumbsUpOutline
  } from 'flowbite-svelte-icons';
  import VideoJs10Player from '$lib/components/players/VideoJs10Player.svelte';
  import SveltePlayer from '$lib/components/players/SveltePlayer.svelte';
  import {
    accentFor,
    formatCount,
    formatDuration,
    formatRelativeDate,
    formatViews,
    initialsFor
  } from '$lib/media';

  interface Detail {
    mediaGuid: string;
    title: string;
    description?: string | null;
    durationSeconds?: number | null;
    releaseDate?: string | null;
    viewCount?: number | null;
    likeCount?: number | null;
    dislikeCount?: number | null;
    commentCount?: number | null;
    wasLive: boolean;
    account: {
      accountId: number;
      accountName: string;
      accountHandle: string;
      followerCount?: number | null;
      isVerified: boolean;
    };
    tags: string[];
  }

  interface Comment {
    commentId: string;
    text: string;
    commentTimestamp: string;
    likeCount?: number | null;
    isPinned: boolean;
    isUploader: boolean;
    account: { accountName: string; accountHandle: string };
  }

  interface UpNextCard {
    mediaGuid: string;
    title: string;
    durationSeconds?: number | null;
    releaseDate?: string | null;
    viewCount?: number | null;
    account: { accountName: string };
  }

  const players = [
    { id: 'videojs', label: 'Video.js 10 (beta)' },
    { id: 'svelte', label: 'Svelte Video Player' }
  ] as const;
  type PlayerId = (typeof players)[number]['id'];

  let playerTab = $state<PlayerId>('videojs');
  let detail = $state<Detail | null>(null);
  let loadError = $state<string | null>(null);
  let comments = $state<Comment[]>([]);
  let commentTotal = $state(0);
  let commentsHaveMore = $state(false);
  let commentPage = $state(1);
  let upNext = $state<UpNextCard[]>([]);
  let descriptionExpanded = $state(false);

  const mediaGuid = $derived(page.params.mediaGuid ?? '');
  const streamUrl = $derived(`/stream/${mediaGuid}`);

  $effect(() => {
    if (mediaGuid) {
      void loadAll(mediaGuid);
    }
  });

  async function loadAll(guid: string) {
    detail = null;
    loadError = null;
    comments = [];
    commentTotal = 0;
    commentsHaveMore = false;
    commentPage = 1;
    descriptionExpanded = false;

    await Promise.all([loadDetail(guid), loadComments(guid, 1), loadUpNext(guid)]);
  }

  async function loadDetail(guid: string) {
    try {
      const response = await fetch(`/api/metadata/${guid}`);
      if (!response.ok) {
        loadError =
          response.status === 401
            ? 'Your session has expired — log in again from the button in the top bar.'
            : response.status === 404
              ? 'This video does not exist on the server.'
              : `Could not load the video (status ${response.status}).`;
        return;
      }
      detail = (await response.json()) as Detail;
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the video.';
    }
  }

  async function loadComments(guid: string, target: number) {
    try {
      const response = await fetch(`/api/metadata/${guid}/comments?page=${target}&pageSize=20`);
      if (!response.ok) {
        return;
      }
      const data = (await response.json()) as {
        items: Comment[];
        page: number;
        totalCount: number;
        hasMore: boolean;
      };
      comments = target === 1 ? data.items : [...comments, ...data.items];
      commentTotal = data.totalCount;
      commentsHaveMore = data.hasMore;
      commentPage = data.page;
    } catch {
      // Comments are secondary; the player is the page's real content.
    }
  }

  async function loadUpNext(guid: string) {
    try {
      const response = await fetch('/api/metadata?page=1&pageSize=12&sortBy=release_date&sortOrder=desc');
      if (!response.ok) {
        return;
      }
      const data = (await response.json()) as { items: UpNextCard[] };
      upNext = data.items.filter((item) => item.mediaGuid !== guid).slice(0, 10);
    } catch {
      // The rail is optional.
    }
  }

  function upNextMeta(card: UpNextCard): string {
    return [formatCount(card.viewCount), formatRelativeDate(card.releaseDate)]
      .filter(Boolean)
      .join(' · ');
  }
</script>

<svelte:head>
  <title>{detail ? `${detail.title} · FrostStream` : 'Watch · FrostStream'}</title>
</svelte:head>

<div class="grid gap-8 xl:grid-cols-[minmax(0,1fr)_360px]">
  <section class="min-w-0" aria-label="Video player">
    <div class="mb-3 flex gap-1 rounded-xl border border-slate-800/70 bg-slate-900/40 p-1" role="tablist" aria-label="Player implementation">
      {#each players as p}
        <button
          type="button"
          role="tab"
          aria-selected={playerTab === p.id}
          onclick={() => (playerTab = p.id)}
          class={[
            'flex-1 rounded-lg px-4 py-2 text-xs font-semibold transition',
            playerTab === p.id
              ? 'bg-blue-500/15 text-blue-400'
              : 'text-slate-500 hover:bg-slate-800/70 hover:text-slate-300'
          ]}
        >
          {p.label}
        </button>
      {/each}
    </div>

    {#if loadError}
      <div
        class="flex aspect-video items-center justify-center gap-2 rounded-2xl border border-red-900/60 bg-red-950/30 p-6 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="h-5 w-5 shrink-0" />
        <span>{loadError}</span>
      </div>
    {:else}
      <div class="aspect-video overflow-hidden rounded-2xl bg-black shadow-2xl shadow-black/30">
        {#key `${mediaGuid}:${playerTab}`}
          {#if playerTab === 'videojs'}
            <VideoJs10Player src={streamUrl} />
          {:else}
            <SveltePlayer src={streamUrl} />
          {/if}
        {/key}
      </div>
    {/if}

    {#if detail}
      <h1 class="mt-4 text-xl font-bold tracking-tight text-white sm:text-2xl">{detail.title}</h1>

      <div class="mt-3 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-3">
          <span
            class={`grid h-10 w-10 shrink-0 place-items-center rounded-full bg-gradient-to-br ${accentFor(detail.account.accountName)} text-xs font-bold text-white`}
          >
            {initialsFor(detail.account.accountName)}
          </span>
          <div class="min-w-0">
            <p class="flex items-center gap-1 text-sm font-semibold text-slate-200">
              {detail.account.accountName}
              {#if detail.account.isVerified}
                <span class="text-blue-400" title="Verified">✓</span>
              {/if}
            </p>
            {#if formatCount(detail.account.followerCount)}
              <p class="text-xs text-slate-500">{formatCount(detail.account.followerCount)} subscribers</p>
            {:else}
              <p class="text-xs text-slate-500">@{detail.account.accountHandle}</p>
            {/if}
          </div>
          <Button
            color="blue"
            class="ml-2 border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400!"
          >
            <PlusOutline class="mr-1 h-3.5 w-3.5" />
            Subscribe
          </Button>
        </div>

        <div class="flex items-center gap-2">
          <div class="flex overflow-hidden rounded-full border border-slate-800 bg-slate-900/70">
            <button
              type="button"
              class="flex items-center gap-1.5 px-4 py-2 text-xs font-semibold text-slate-300 transition hover:bg-slate-800"
            >
              <ThumbsUpOutline class="h-4 w-4" />
              {formatCount(detail.likeCount) ?? '—'}
            </button>
            <span class="my-1.5 w-px bg-slate-800"></span>
            <button
              type="button"
              class="px-3 py-2 text-slate-400 transition hover:bg-slate-800"
              aria-label="Dislike"
            >
              <ThumbsDownOutline class="h-4 w-4" />
            </button>
          </div>
          <Button
            color="dark"
            class="border-slate-800! bg-slate-900/70! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
          >
            <ShareNodesOutline class="mr-1.5 h-4 w-4" />
            Share
          </Button>
          <Button
            color="dark"
            class="border-slate-800! bg-slate-900/70! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
          >
            <FolderOutline class="mr-1.5 h-4 w-4" />
            Save
          </Button>
          <Button
            color="dark"
            class="h-9 w-9 border-slate-800! bg-slate-900/70! p-2! text-slate-400! hover:bg-slate-800!"
            aria-label="More actions"
          >
            <DotsHorizontalOutline class="h-4 w-4" />
          </Button>
        </div>
      </div>

      <div class="mt-4 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm font-semibold text-slate-300">
          {#if formatViews(detail.viewCount)}<span>{formatViews(detail.viewCount)}</span>{/if}
          {#if formatRelativeDate(detail.releaseDate)}<span class="text-slate-500">·</span><span>{formatRelativeDate(detail.releaseDate)}</span>{/if}
          {#each detail.tags.slice(0, 6) as tag}
            <span class="rounded-full bg-slate-800/80 px-2.5 py-0.5 text-xs font-medium text-slate-400">
              #{tag}
            </span>
          {/each}
        </div>
        {#if detail.description}
          <p
            class={[
              'mt-3 whitespace-pre-line text-sm leading-6 text-slate-400',
              !descriptionExpanded && 'line-clamp-3'
            ]}
          >
            {detail.description}
          </p>
          <button
            type="button"
            onclick={() => (descriptionExpanded = !descriptionExpanded)}
            class="mt-2 flex items-center gap-1 text-xs font-semibold text-slate-500 transition hover:text-slate-300"
          >
            {descriptionExpanded ? 'Show less' : 'Show more'}
            {#if descriptionExpanded}
              <ChevronUpOutline class="h-3 w-3" />
            {:else}
              <ChevronDownOutline class="h-3 w-3" />
            {/if}
          </button>
        {/if}
      </div>

      <section class="mt-8" aria-label="Comments">
        <h2 class="text-lg font-bold text-white">
          {commentTotal > 0 ? `${commentTotal} comments` : 'Comments'}
        </h2>

        {#if comments.length === 0}
          <p class="mt-4 text-sm text-slate-500">No comments were archived for this video.</p>
        {:else}
          <ul class="mt-5 space-y-6">
            {#each comments as comment (comment.commentId)}
              <li class="flex gap-3">
                <span
                  class={`mt-0.5 grid h-9 w-9 shrink-0 place-items-center rounded-full bg-gradient-to-br ${accentFor(comment.account.accountName)} text-[10px] font-bold text-white`}
                >
                  {initialsFor(comment.account.accountName)}
                </span>
                <div class="min-w-0">
                  <p class="flex flex-wrap items-center gap-2 text-xs">
                    <span class="font-semibold text-slate-200">{comment.account.accountName}</span>
                    {#if comment.isUploader}
                      <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">Creator</span>
                    {/if}
                    {#if comment.isPinned}
                      <span class="text-[10px] font-semibold text-slate-500">Pinned</span>
                    {/if}
                    <span class="text-slate-600">{formatRelativeDate(comment.commentTimestamp)}</span>
                  </p>
                  <p class="mt-1 whitespace-pre-line text-sm leading-6 text-slate-300">{comment.text}</p>
                  {#if comment.likeCount}
                    <p class="mt-1.5 flex items-center gap-1.5 text-xs text-slate-600">
                      <ThumbsUpOutline class="h-3.5 w-3.5" />
                      {formatCount(comment.likeCount)}
                    </p>
                  {/if}
                </div>
              </li>
            {/each}
          </ul>

          {#if commentsHaveMore}
            <Button
              color="dark"
              onclick={() => loadComments(mediaGuid, commentPage + 1)}
              class="mt-6 border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
            >
              Load more comments
            </Button>
          {/if}
        {/if}
      </section>
    {:else if !loadError}
      <div class="mt-10 flex justify-center">
        <Spinner size="6" />
      </div>
    {/if}
  </section>

  <aside aria-label="Up next">
    <h2 class="text-sm font-bold uppercase tracking-[0.08em] text-slate-500">Up next</h2>
    <ul class="mt-4 space-y-4">
      {#each upNext as card (card.mediaGuid)}
        <li>
          <a href={`/watch/${card.mediaGuid}`} class="group flex gap-3 rounded-xl focus-visible:outline-offset-4">
            <span
              class={`relative block aspect-video w-40 shrink-0 overflow-hidden rounded-xl bg-gradient-to-br ${accentFor(card.mediaGuid)} shadow-lg shadow-black/20`}
            >
              <span class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-xl font-black text-white/15">
                {initialsFor(card.account.accountName)}
              </span>
              {#if formatDuration(card.durationSeconds)}
                <span class="absolute bottom-1.5 right-1.5 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-semibold text-white">
                  {formatDuration(card.durationSeconds)}
                </span>
              {/if}
            </span>
            <span class="min-w-0">
              <span class="line-clamp-2 text-sm font-semibold leading-snug text-slate-200 group-hover:text-white">
                {card.title}
              </span>
              <span class="mt-1 block truncate text-xs text-slate-500">{card.account.accountName}</span>
              {#if upNextMeta(card)}
                <span class="mt-0.5 block truncate text-xs text-slate-600">{upNextMeta(card)}</span>
              {/if}
            </span>
          </a>
        </li>
      {:else}
        <li class="text-sm text-slate-600">Nothing else on the server yet.</li>
      {/each}
    </ul>
  </aside>
</div>
