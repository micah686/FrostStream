<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Spinner } from 'flowbite-svelte';
  import {
    BookmarkOutline,
    CameraPhotoOutline,
    ChevronRightOutline,
    ClockOutline,
    GlobeOutline,
    PlaySolid
  } from 'flowbite-svelte-icons';
  import { accentFor, formatDuration, formatRelativeDate, initialsFor } from '$lib/media';
  import { listInProgress } from '$lib/api/watchState';

  interface ContinueCard {
    mediaGuid: string;
    positionSeconds: number;
    durationSeconds: number | null;
    progressPercent: number;
    lastPlayedAt: string;
    title: string;
    creator: string;
    hasThumbnail: boolean;
  }

  interface MediaSummary {
    title: string;
    thumbnailStoragePath?: string | null;
    durationSeconds?: number | null;
    account: { accountName: string };
  }

  const categories: string[] = [
    'All',
    'Continue watching',
    'Subscriptions',
    'Music',
    'Field recordings',
    'Tech',
    'Photography',
    'Pixel art',
    'Long-form',
    'Live'
  ];

  let continueCards = $state<ContinueCard[]>([]);
  let continueLoading = $state(true);

  onMount(() => {
    void loadContinueWatching();
  });

  async function loadContinueWatching() {
    try {
      const states = await listInProgress(12);
      const cards = await Promise.all(
        states.map(async (state): Promise<ContinueCard | null> => {
          let summary: MediaSummary | null = null;
          try {
            const response = await fetch(`/api/metadata/${state.mediaGuid}`);
            summary = response.ok ? ((await response.json()) as MediaSummary) : null;
          } catch {
            summary = null;
          }
          if (!summary) {
            return null;
          }

          const duration = state.durationSeconds ?? summary.durationSeconds ?? null;
          const position = state.positionSeconds ?? 0;
          return {
            mediaGuid: state.mediaGuid,
            positionSeconds: position,
            durationSeconds: duration,
            progressPercent: duration && duration > 0 ? Math.min(100, (position / duration) * 100) : 0,
            lastPlayedAt: state.lastPlayedAt,
            title: summary.title,
            creator: summary.account.accountName,
            hasThumbnail: Boolean(summary.thumbnailStoragePath)
          };
        })
      );
      continueCards = cards.filter((card): card is ContinueCard => card !== null);
    } catch {
      // The section quietly disappears when the list can't be loaded (e.g. logged out).
      continueCards = [];
    } finally {
      continueLoading = false;
    }
  }

  function resumeHref(card: ContinueCard): string {
    return `/watch/${card.mediaGuid}?t=${Math.floor(card.positionSeconds)}`;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<svelte:head>
  <title>FrostStream</title>
  <meta
    name="description"
    content="A dark media dashboard shell for browsing a personal FrostStream library."
  />
</svelte:head>

<section
  class="relative overflow-hidden rounded-2xl border border-blue-400/5 bg-[linear-gradient(115deg,#1b3154_0%,#203f75_44%,#271534_100%)] shadow-2xl shadow-black/20"
  aria-labelledby="featured-title"
>
  <div
    class="pointer-events-none absolute inset-0 opacity-20 [background-image:linear-gradient(rgba(255,255,255,.04)_1px,transparent_1px),linear-gradient(90deg,rgba(255,255,255,.04)_1px,transparent_1px)] [background-size:4px_4px]"
  ></div>
  <div class="relative grid min-h-[360px] gap-8 p-7 sm:p-10 xl:grid-cols-[1.1fr_1fr] xl:p-10">
    <div class="flex max-w-2xl flex-col justify-center">
      <div class="mb-5 flex items-center gap-2">
        <Badge
          rounded
          color="blue"
          class="bg-blue-300/12! px-3! py-1.5! text-[10px]! font-bold! tracking-[0.08em]! text-blue-100!"
        >
          FEATURED
        </Badge>
        <span class="text-[11px] font-semibold tracking-wide text-blue-200/80">PIXELFORGE</span>
      </div>
      <h1
        id="featured-title"
        class="max-w-xl text-4xl font-black leading-[1.08] tracking-[-0.04em] text-white sm:text-5xl"
      >
        CRT pixel art workflow without the eye strain
      </h1>
      <p class="mt-4 max-w-xl text-sm leading-6 text-blue-100/75 sm:text-base">
        A patient 17-minute walkthrough of the workflow used to make all the pixel art on this
        server. CRT-friendly palettes, no eye strain, no hype, just small decisions that compound.
      </p>
      <div class="mt-6 flex flex-wrap gap-3">
        <Button
          color="blue"
          class="border-0! bg-blue-500! px-5! py-2.5! font-semibold! shadow-lg shadow-blue-950/30 hover:bg-blue-400!"
        >
          <PlaySolid class="mr-2 h-4 w-4" />
          Watch now
        </Button>
        <Button
          color="dark"
          class="border-blue-200/10! bg-white/6! px-5! py-2.5! font-semibold! text-blue-50! hover:bg-white/12!"
        >
          <BookmarkOutline class="mr-2 h-4 w-4" />
          Watch later
        </Button>
      </div>
    </div>

    <div
      class="relative hidden min-h-[280px] overflow-hidden rounded-2xl border border-lime-300/5 bg-[#294d0f] shadow-xl shadow-black/20 sm:block"
      aria-label="Featured media preview placeholder"
    >
      <Badge
        color="blue"
        class="absolute left-3 top-3 z-10 rounded-md! px-2! py-1! text-[10px]! font-bold!"
      >
        NEW
      </Badge>
      <div
        class="absolute inset-[15%_12%] overflow-hidden rounded-xl bg-lime-100/[0.035] shadow-inner shadow-black/10"
      >
        <div class="absolute inset-x-0 top-1/3 border-t border-lime-100/10"></div>
        <div class="absolute inset-x-0 top-2/3 border-t border-lime-100/10"></div>
        <button
          type="button"
          aria-label="Play featured media"
          class="absolute left-1/2 top-1/2 grid h-16 w-16 -translate-x-1/2 -translate-y-1/2 place-items-center rounded-full bg-white text-[#11151c] shadow-xl transition hover:scale-105 hover:bg-blue-50"
        >
          <PlaySolid class="ml-1 h-7 w-7" />
        </button>
      </div>
      <span
        class="absolute bottom-3 right-3 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-semibold text-white"
      >
        17:32
      </span>
    </div>
  </div>
</section>

<nav class="mt-7 flex gap-2 overflow-x-auto pb-2" aria-label="Media categories">
  {#each categories as category, index}
    <Button
      pill
      color={index === 0 ? 'light' : 'dark'}
      size="xs"
      class={[
        'shrink-0 border-0! px-4! py-2! text-xs! font-medium!',
        index === 0
          ? 'bg-slate-100! text-slate-900! hover:bg-white!'
          : 'bg-slate-800/80! text-slate-300! hover:bg-slate-700!'
      ]}
    >
      {category}
    </Button>
  {/each}
</nav>

<section class="mt-5" aria-labelledby="continue-watching-title">
  <div class="mb-4 flex items-end justify-between gap-4">
    <div>
      <h2 id="continue-watching-title" class="text-xl font-bold tracking-tight text-slate-100">
        Continue watching
      </h2>
      <p class="mt-1 text-sm text-slate-500">Pick up where you left off across devices</p>
    </div>
    <button
      type="button"
      class="hidden items-center gap-1 rounded-lg px-2 py-1 text-xs font-semibold text-slate-500 transition hover:text-blue-400 sm:flex"
    >
      See all
      <ChevronRightOutline class="h-3 w-3" />
    </button>
  </div>

  {#if continueLoading}
    <div class="flex justify-center py-12">
      <Spinner size="8" />
    </div>
  {:else if continueCards.length === 0}
    <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <ClockOutline class="mx-auto h-10 w-10 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">Nothing in progress</p>
      <p class="mt-1 text-sm text-slate-500">Start a video from your library and it will show up here.</p>
    </div>
  {:else}
    <div class="grid gap-5 sm:grid-cols-2 xl:grid-cols-4">
      {#each continueCards as item (item.mediaGuid)}
        <article class="group min-w-0">
          <a
            href={resumeHref(item)}
            class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(item.mediaGuid)} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
            aria-label={`Resume ${item.title}`}
          >
            <span
              class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-3xl font-black text-white/15"
            >
              {initialsFor(item.creator)}
            </span>
            {#if item.hasThumbnail}
              <img
                src={`/stream/${item.mediaGuid}/thumbnail`}
                alt=""
                loading="lazy"
                decoding="async"
                class="absolute inset-0 h-full w-full object-cover"
                onerror={hideBrokenImage}
              />
            {/if}
            {#if formatDuration(item.durationSeconds)}
              <span
                class="absolute bottom-3 right-3 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-semibold text-white"
              >
                {formatDuration(item.durationSeconds)}
              </span>
            {/if}
            <span class="absolute inset-x-0 bottom-0 h-1 bg-black/35">
              <span class="block h-full bg-blue-500" style={`width: ${item.progressPercent}%`}></span>
            </span>
            <span
              class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-950 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
            >
              <PlaySolid class="ml-0.5 h-5 w-5" />
            </span>
          </a>
          <div class="mt-3 min-w-0 px-1">
            <h3 class="truncate text-sm font-semibold text-slate-200">{item.title}</h3>
            <p class="mt-1 truncate text-xs text-slate-500">
              {[
                item.creator,
                formatDuration(item.positionSeconds) ? `at ${formatDuration(item.positionSeconds)}` : null,
                formatRelativeDate(item.lastPlayedAt)
              ]
                .filter(Boolean)
                .join(' · ')}
            </p>
          </div>
        </article>
      {/each}
    </div>
  {/if}
</section>

<section class="mt-10 grid gap-4 md:grid-cols-3" aria-label="Library overview">
  <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
    <CameraPhotoOutline class="h-5 w-5 text-blue-400" />
    <p class="mt-4 text-2xl font-bold text-white">248</p>
    <p class="mt-1 text-xs text-slate-500">Videos in your library</p>
  </div>
  <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
    <GlobeOutline class="h-5 w-5 text-violet-400" />
    <p class="mt-4 text-2xl font-bold text-white">12</p>
    <p class="mt-1 text-xs text-slate-500">Channels followed</p>
  </div>
  <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
    <ClockOutline class="h-5 w-5 text-emerald-400" />
    <p class="mt-4 text-2xl font-bold text-white">36h</p>
    <p class="mt-1 text-xs text-slate-500">Ready to watch offline</p>
  </div>
</section>
