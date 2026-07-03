<script lang="ts">
  import { onMount } from 'svelte';
  import { Spinner } from 'flowbite-svelte';
  import {
    CameraPhotoOutline,
    ChevronRightOutline,
    ClockOutline,
    GlobeOutline,
    PlaySolid
  } from 'flowbite-svelte-icons';
  import { accentFor, formatBytes, formatDuration, formatRelativeDate, initialsFor } from '$lib/media';
  import { getGlobalStatistics, type StatisticsOverview } from '$lib/api/statistics';
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

  let continueCards = $state<ContinueCard[]>([]);
  let continueLoading = $state(true);
  let overview = $state<StatisticsOverview | null>(null);

  onMount(() => {
    void loadContinueWatching();
    void loadOverview();
  });

  async function loadOverview() {
    try {
      overview = await getGlobalStatistics();
    } catch {
      // The overview cards are secondary; continue watching remains the primary page content.
      overview = null;
    }
  }

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

<section aria-labelledby="continue-watching-title">
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
                src={`/api/watch/${item.mediaGuid}/thumbnail`}
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
    <p class="mt-4 text-2xl font-bold text-white">{overview?.inventory.totalMedia.toLocaleString() ?? '-'}</p>
    <p class="mt-1 text-xs text-slate-500">Videos in your library</p>
  </div>
  <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
    <GlobeOutline class="h-5 w-5 text-violet-400" />
    <p class="mt-4 text-2xl font-bold text-white">{overview?.inventory.totalChannels.toLocaleString() ?? '-'}</p>
    <p class="mt-1 text-xs text-slate-500">Channels followed</p>
  </div>
  <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
    <ClockOutline class="h-5 w-5 text-emerald-400" />
    <p class="mt-4 text-2xl font-bold text-white">{overview ? formatBytes(overview.inventory.totalBytes) : '-'}</p>
    <p class="mt-1 text-xs text-slate-500">Ready to watch offline</p>
  </div>
</section>
