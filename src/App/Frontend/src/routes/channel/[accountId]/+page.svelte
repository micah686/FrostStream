<script lang="ts">
  import { page } from '$app/state';
  import { onDestroy } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    ArrowUpRightFromSquareOutline,
    ChevronDownOutline,
    ChevronLeftOutline,
    ChevronRightOutline,
    ChevronUpOutline,
    ExclamationCircleOutline,
    FileCopyOutline,
    HeadphonesOutline,
    ImageOutline,
    MusicOutline,
    PlaySolid,
    RectangleListOutline
  } from 'flowbite-svelte-icons';
  import { accentFor, formatBytes, formatCount, formatDuration, formatRelativeDate, formatViews, initialsFor } from '$lib/media';
  import { refreshAccountAssets } from '$lib/api/metadata';
  import {
    createPodcastFeedLink,
    encodeChannelAudio,
    getChannelAudioStatus,
    renditionProgressStreamUrl,
    type ChannelAudioStatus,
    type RenditionProgressFrame
  } from '$lib/api/channelAudio';
  import { readEventStream } from '$lib/sse/eventStream';
  import TargetNotePanel from '$lib/components/TargetNotePanel.svelte';
  import VideoJs10AudioPlayer from '$lib/components/players/VideoJs10AudioPlayer.svelte';
  import {
    getChannelStatistics,
    listChannelStatistics,
    type ChannelStatisticsDetail,
    type ChannelStatisticsSummary
  } from '$lib/api/statistics';

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
  let assetRefreshBusy = $state(false);
  let assetRefreshNotice = $state<string | null>(null);
  let statistics = $state<ChannelStatisticsDetail | null>(null);
  let statisticsLoading = $state(false);
  let statisticsError = $state<string | null>(null);
  let statisticsExpanded = $state(false);
  let channelAudio = $state<ChannelAudioStatus | null>(null);
  let channelAudioLoading = $state(false);
  let channelAudioBusy = $state(false);
  let channelAudioNotice = $state<string | null>(null);
  let selectedStorageKey = $state('');
  let audioPlayerOpen = $state(false);
  let audioIndex = $state(0);
  let podcastBusy = $state(false);
  let podcastFeedUrl = $state<string | null>(null);
  let audioPollTimer: ReturnType<typeof setTimeout> | null = null;
  let renditionStreamController: AbortController | null = null;
  let liveRenditionProgress = $state<Record<string, RenditionProgressFrame>>({});

  const accountId = $derived(page.params.accountId ?? '');
  const totalPages = $derived(Math.max(1, Math.ceil(totalCount / pageSize)));
  const bannerUrl = $derived(
    account?.bannerStoragePath && !bannerBroken ? `/api/media/watch/accounts/${account.accountId}/banner` : null
  );
  const avatarUrl = $derived(
    account?.avatarStoragePath && !avatarBroken ? `/api/media/watch/accounts/${account.accountId}/avatar` : null
  );
  const readyAudioItems = $derived(
    channelAudio?.items.filter((item) => item.rendition?.status === 'Ready') ?? []
  );
  const currentAudioItem = $derived(readyAudioItems[audioIndex] ?? null);
  const audioComplete = $derived(
    channelAudio !== null && channelAudio.totalCount > 0 && channelAudio.readyCount === channelAudio.totalCount
  );
  const audioProgress = $derived(
    channelAudio?.totalCount ? Math.round((channelAudio.readyCount / channelAudio.totalCount) * 100) : 0
  );

  onDestroy(() => {
    if (audioPollTimer) clearTimeout(audioPollTimer);
    closeRenditionStream();
  });

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
    statisticsExpanded = false;
    sort = 'release_date:desc';
    items = [];
    totalCount = 0;
    hasMore = false;

    channelAudio = null;
    channelAudioNotice = null;
    selectedStorageKey = '';
    audioPlayerOpen = false;
    podcastFeedUrl = null;

    await Promise.all([loadAccount(id), loadMedia(id, 1), loadStatistics(id), loadChannelAudio(id)]);
  }

  async function loadChannelAudio(id: string, quiet = false) {
    const parsedId = Number(id);
    if (!Number.isFinite(parsedId)) return;
    if (!quiet) channelAudioLoading = true;

    try {
      channelAudio = await getChannelAudioStatus(parsedId, selectedStorageKey || undefined);
      scheduleAudioPoll(id);
    } catch (err) {
      if (!quiet) {
        channelAudioNotice = err instanceof Error ? err.message : 'Could not load channel audio status.';
      }
    } finally {
      if (!quiet) channelAudioLoading = false;
    }
  }

  function scheduleAudioPoll(id: string) {
    if (audioPollTimer) clearTimeout(audioPollTimer);
    audioPollTimer = null;
    const encodesInFlight = channelAudio !== null && channelAudio.pendingCount + channelAudio.processingCount > 0;
    syncRenditionStream(encodesInFlight);
    if (!encodesInFlight) return;
    audioPollTimer = setTimeout(() => void loadChannelAudio(id, true), 2000);
  }

  // Live per-item encode progress over SSE, on top of the coarse 2s count polling: the poll stays
  // as the resilient fallback, the stream adds percent/speed and immediate refresh on completion.
  function syncRenditionStream(shouldStream: boolean) {
    if (!shouldStream) {
      closeRenditionStream();
      return;
    }
    if (renditionStreamController) return;
    const controller = new AbortController();
    renditionStreamController = controller;
    void (async () => {
      try {
        await readEventStream(
          renditionProgressStreamUrl(),
          {
            onEvent: (event) => {
              if (event.event !== 'progress') return;
              applyRenditionFrame(JSON.parse(event.data) as RenditionProgressFrame);
            }
          },
          controller.signal
        );
      } catch {
        // Live progress is advisory; the status poll keeps working without it.
      } finally {
        if (renditionStreamController === controller) renditionStreamController = null;
      }
    })();
  }

  function closeRenditionStream() {
    renditionStreamController?.abort();
    renditionStreamController = null;
    liveRenditionProgress = {};
  }

  function applyRenditionFrame(frame: RenditionProgressFrame) {
    if (frame.kind !== 'Audio') return;
    if (!channelAudio?.items.some((item) => item.mediaGuid === frame.mediaGuid)) return;

    if (frame.phase === 'Ready' || frame.phase === 'Failed') {
      const rest = { ...liveRenditionProgress };
      delete rest[frame.mediaGuid];
      liveRenditionProgress = rest;
      // Counts changed server-side; refresh right away instead of waiting out the poll.
      void loadChannelAudio(accountId, true);
      return;
    }

    liveRenditionProgress = { ...liveRenditionProgress, [frame.mediaGuid]: frame };
  }

  function renditionItemTitle(frame: RenditionProgressFrame): string {
    return channelAudio?.items.find((item) => item.mediaGuid === frame.mediaGuid)?.title ?? 'Encoding';
  }

  function renditionProgressLine(frame: RenditionProgressFrame): string {
    const parts = [frame.phase];
    if (frame.percent !== null) parts.push(`${Math.round(frame.percent)}%`);
    if (frame.speedX !== null) parts.push(`${frame.speedX.toFixed(1)}x`);
    const eta = frame.etaSeconds !== null ? formatDuration(Math.round(frame.etaSeconds)) : null;
    if (eta) parts.push(`eta ${eta}`);
    return parts.join(' · ');
  }

  async function encodeAudio() {
    if (!account || channelAudioBusy) return;
    channelAudioBusy = true;
    channelAudioNotice = null;
    try {
      channelAudio = await encodeChannelAudio(account.accountId, selectedStorageKey || undefined);
      const scope = selectedStorageKey ? ` on storage key "${selectedStorageKey}"` : '';
      channelAudioNotice = channelAudio.totalCount === 0
        ? `This channel has no archived media to encode${scope}.`
        : `Queued Opus audio for ${channelAudio.totalCount.toLocaleString()} archived items${scope}.`;
      scheduleAudioPoll(String(account.accountId));
    } catch (err) {
      channelAudioNotice = err instanceof Error ? err.message : 'Could not queue channel audio encoding.';
    } finally {
      channelAudioBusy = false;
    }
  }

  function changeAudioStorageKey() {
    if (!account) return;
    void loadChannelAudio(String(account.accountId));
  }

  function playAudio() {
    if (!audioComplete) return;
    audioIndex = 0;
    audioPlayerOpen = true;
  }

  function moveAudio(offset: number) {
    if (readyAudioItems.length === 0) return;
    audioIndex = (audioIndex + offset + readyAudioItems.length) % readyAudioItems.length;
  }

  function audioSource(item: NonNullable<typeof currentAudioItem>): string {
    const version = item.rendition?.sourceVersion;
    const query = new URLSearchParams({ audio: 'true', prepare: 'false' });
    if (version !== undefined) query.set('version', String(version));
    return `/api/media/watch/${encodeURIComponent(item.mediaGuid)}?${query}`;
  }

  function audioDownloadName(title: string): string {
    const baseName = title
      .replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_')
      .replace(/[. ]+$/g, '')
      .trim()
      .slice(0, 180);
    return `${baseName || 'audio'}.opus`;
  }

  async function copyPodcastFeed() {
    if (!account || podcastBusy) return;
    podcastBusy = true;
    channelAudioNotice = null;
    try {
      const result = await createPodcastFeedLink(account.accountId);
      podcastFeedUrl = result.feedUrl;
      await loadChannelAudio(String(account.accountId), true);
      await navigator.clipboard.writeText(result.feedUrl);
      channelAudioNotice = 'Podcast RSS URL copied to the clipboard.';
    } catch (err) {
      channelAudioNotice = err instanceof Error ? err.message : 'Could not create the podcast RSS URL.';
    } finally {
      podcastBusy = false;
    }
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

  async function loadStatistics(id: string) {
    statisticsLoading = true;
    statisticsError = null;
    statistics = null;

    try {
      const source = await findChannelSource(Number(id));
      if (!source || source.creatorSourceId === null) {
        return;
      }
      statistics = await getChannelStatistics(source.creatorSourceId);
    } catch (err) {
      statisticsError = err instanceof Error ? err.message : 'Could not load this creator statistics.';
    } finally {
      statisticsLoading = false;
    }
  }

  async function findChannelSource(id: number): Promise<ChannelStatisticsSummary | null> {
    if (!Number.isFinite(id)) {
      return null;
    }

    let currentPage = 1;
    let hasNextPage = true;
    while (hasNextPage) {
      const response = await listChannelStatistics({
        page: currentPage,
        pageSize: 100,
        sortBy: 'name',
        sortOrder: 'asc'
      });
      const match = response.items.find((item) => item.accountId === id);
      if (match) {
        return match;
      }
      hasNextPage = response.hasMore;
      currentPage += 1;
    }

    return null;
  }

  function changeSort() {
    void loadMedia(accountId, 1);
  }

  async function refreshAssets(force: boolean) {
    if (!account || assetRefreshBusy) {
      return;
    }
    assetRefreshBusy = true;
    assetRefreshNotice = null;
    try {
      await refreshAccountAssets(account.accountId, force);
      assetRefreshNotice = `Asset refresh queued${force ? ' (forced)' : ''} — the avatar and banner will update shortly.`;
    } catch (err) {
      assetRefreshNotice = err instanceof Error ? err.message : 'Could not queue the asset refresh.';
    } finally {
      assetRefreshBusy = false;
    }
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
    return card.thumbnailStoragePath ? `/api/media/watch/${card.mediaGuid}/thumbnail` : null;
  }

  function percent(value: number): string {
    return `${Math.round(value)}%`;
  }

  function statusTotal(current: ChannelStatisticsDetail): number {
    return (
      current.summary.availableCount +
      current.ignoredCount +
      current.unavailableCount +
      current.removedCount
    );
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
    class="flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
    role="alert"
  >
    <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
    <span>{loadError}</span>
  </div>
{:else if !account}
  <div class="mt-16 flex justify-center">
    <span class="loading loading-spinner loading-md"></span>
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
        class={`relative -mt-14 grid h-28 w-28 shrink-0 place-items-center overflow-hidden rounded-full border-4 border-base-300 bg-gradient-to-br text-3xl font-bold text-white sm:-mt-16 sm:h-36 sm:w-36 sm:text-4xl ${accentFor(account.accountName)} shadow-xl shadow-black/30`}
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
          <h1 id="channel-title" class="text-2xl font-bold tracking-tight text-base-content sm:text-3xl">
            {account.accountName}
          </h1>
          {#if account.isVerified}
            <span class="text-lg text-primary" title="Verified">✓</span>
          {/if}
          <span
            class="rounded-md bg-base-300/80 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-base-content/60"
          >
            {account.platform}
          </span>
        </div>
        <p class="mt-1 text-sm text-base-content/50">@{account.accountHandle}</p>
        <p class="mt-1.5 text-sm text-base-content/60">{statLine(account)}</p>

        {#if account.description}
          <p
            class={[
              'mt-3 max-w-3xl whitespace-pre-line text-sm leading-6 text-base-content/60',
              !descriptionExpanded && 'line-clamp-2'
            ]}
          >
            {account.description}
          </p>
          <button
            type="button"
            onclick={() => (descriptionExpanded = !descriptionExpanded)}
            class="mt-1.5 flex items-center gap-1 text-xs font-semibold text-base-content/50 transition hover:text-base-content/80"
          >
            {descriptionExpanded ? 'Show less' : 'Show more'}
            {#if descriptionExpanded}
              <ChevronUpOutline class="h-3 w-3" />
            {:else}
              <ChevronDownOutline class="h-3 w-3" />
            {/if}
          </button>
        {/if}

        <div class="mt-4 max-w-3xl">
          <TargetNotePanel
            targetType="channel"
            targetId={String(account.accountId)}
            targetLabel="Channel"
            initialNote={account.userNote ?? null}
            onChange={(note) => {
              if (account) {
                account = { ...account, userNote: note };
              }
            }}
          />
        </div>
      </div>

      <div class="flex shrink-0 flex-col gap-2 sm:items-end">
        {#if account.accountUrl}
          <a class="btn btn-sm btn-neutral text-xs" href={account.accountUrl} target="_blank" rel="noopener noreferrer">
            <ArrowUpRightFromSquareOutline class="mr-1.5 h-3.5 w-3.5" />
            View on {account.platform}
          </a>
        {/if}
        <button class="btn btn-sm btn-neutral text-xs" disabled={assetRefreshBusy} onclick={(event: MouseEvent) => refreshAssets(event.shiftKey)} title="Queue a refresh of the avatar and banner (hold Shift to force re-download)">
          {#if assetRefreshBusy}
            <span class="loading loading-spinner loading-xs mr-1.5"></span>
          {:else}
            <ImageOutline class="mr-1.5 h-3.5 w-3.5" />
          {/if}
          Refresh assets
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={channelAudioBusy || channelAudioLoading} onclick={encodeAudio}>
          {#if channelAudioBusy}
            <span class="loading loading-spinner loading-xs mr-1.5"></span>
          {:else}
            <MusicOutline class="mr-1.5 h-3.5 w-3.5" />
          {/if}
          Encode as audio
        </button>
        {#if channelAudio && channelAudio.availableStorageKeys.length > 0}
          <Select items={[
 { value: '', name: 'All storage keys' },
              ...channelAudio.availableStorageKeys.map((key) => ({ value: key, name: key }))
            ]} bind:value={selectedStorageKey} onchange={changeAudioStorageKey} disabled={channelAudioBusy || channelAudioLoading} aria-label="Storage key to encode" title="Scope encoding to a single storage key, or encode across all of them" class="w-40 text-xs" />
        {/if}
        <button class="btn btn-sm btn-neutral text-xs" disabled={!audioComplete} onclick={playAudio} title={audioComplete ? 'Play every archived video as Opus audio' : 'Encode every item before starting the complete channel playlist'}>
          <HeadphonesOutline class="mr-1.5 h-3.5 w-3.5" />
          Play as audio
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={podcastBusy} onclick={copyPodcastFeed} title="Create and copy a channel-scoped podcast subscription URL">
          {#if podcastBusy}
            <span class="loading loading-spinner loading-xs mr-1.5"></span>
          {:else}
            <FileCopyOutline class="mr-1.5 h-3.5 w-3.5" />
          {/if}
          Copy podcast RSS
        </button>
        {#if channelAudio && channelAudio.totalCount > 0}
          <div class="w-full max-w-60" aria-label={`Audio encoding ${audioProgress}% complete`}>
            <div class="flex justify-between gap-3 text-[11px] text-base-content/50">
              <span>{channelAudio.readyCount.toLocaleString()} / {channelAudio.totalCount.toLocaleString()} encoded</span>
              <span>{audioProgress}%</span>
            </div>
            <div class="mt-1 h-1.5 overflow-hidden rounded-full bg-base-300">
              <div class="h-full rounded-full bg-success transition-all" style={`width: ${audioProgress}%`}></div>
            </div>
            {#if channelAudio.processingCount || channelAudio.pendingCount}
              <p class="mt-1 text-[11px] text-base-content/40">
                {channelAudio.processingCount} processing · {channelAudio.pendingCount} queued
              </p>
              {#each Object.values(liveRenditionProgress) as frame (frame.renditionId)}
                <p class="mt-0.5 truncate text-[11px] text-base-content/50" title={renditionItemTitle(frame)}>
                  {renditionItemTitle(frame)} — {renditionProgressLine(frame)}
                </p>
              {/each}
            {:else if channelAudio.failedCount}
              <p class="mt-1 text-[11px] text-error">{channelAudio.failedCount} failed · Encode as audio retries them</p>
            {/if}
          </div>
        {/if}
        {#if assetRefreshNotice}
          <p class="max-w-60 text-xs text-base-content/50 sm:text-right">{assetRefreshNotice}</p>
        {/if}
        {#if channelAudioNotice}
          <p class="max-w-60 text-xs text-base-content/50 sm:text-right">{channelAudioNotice}</p>
        {/if}
        {#if podcastFeedUrl}
          <a class="max-w-60 truncate text-xs text-primary hover:text-primary" href={podcastFeedUrl} target="_blank" rel="noopener noreferrer">
            Open RSS feed
          </a>
        {/if}
      </div>
    </div>

    {#if audioPlayerOpen && currentAudioItem}
      <section class="mt-6 border-y border-base-300/80 bg-base-200/35 px-4 py-4" aria-labelledby="channel-audio-player-title">
        <div class="mx-auto flex max-w-4xl flex-col gap-3 sm:flex-row sm:items-center">
          <div class="min-w-0 flex-1">
            <p class="text-[11px] font-semibold uppercase tracking-wide text-success">
              Channel audio · {audioIndex + 1} of {readyAudioItems.length}
            </p>
            <h2 id="channel-audio-player-title" class="mt-1 truncate text-sm font-semibold text-base-content">
              {currentAudioItem.title}
            </h2>
          </div>
          <div class="w-full min-w-0 sm:w-[38rem]">
            {#key currentAudioItem.mediaGuid}
              <VideoJs10AudioPlayer
                src={audioSource(currentAudioItem)}
                downloadFileName={audioDownloadName(currentAudioItem.title)}
                autoplay
                onPreviousTrack={() => moveAudio(-1)}
                onNextTrack={() => moveAudio(1)}
                onEnded={() => moveAudio(1)}
              />
            {/key}
          </div>
        </div>
      </section>
    {/if}

    <div class="mt-8 flex flex-wrap items-center justify-between gap-3 border-t border-base-300/70 pt-5">
      <div class="min-w-0">
        <p class="text-sm text-base-content/50">
          {mediaLoading ? 'Loading…' : `${totalCount} ${totalCount === 1 ? 'video' : 'videos'} on the server`}
        </p>
        {#if statistics}
          <p class="mt-1 text-xs text-base-content/40">
            {percent(statistics.summary.downloadedPercent)} downloaded · {formatBytes(statistics.summary.totalBytes)}
          </p>
        {:else if statisticsLoading}
          <p class="mt-1 text-xs text-base-content/40">Loading channel statistics…</p>
        {:else if statisticsError}
          <p class="mt-1 text-xs text-error">Channel statistics unavailable</p>
        {/if}
      </div>
      <div class="flex flex-wrap items-center gap-2">
        {#if statistics || statisticsLoading || statisticsError}
          <button class="btn btn-sm btn-neutral text-xs" disabled={statisticsLoading && !statistics} aria-expanded={statisticsExpanded} aria-controls="channel-statistics-panel" onclick={() => (statisticsExpanded = !statisticsExpanded)}>
            {#if statisticsLoading && !statistics}
              <span class="loading loading-spinner loading-xs mr-1.5"></span>
            {:else if statisticsExpanded}
              <ChevronUpOutline class="mr-1 h-3.5 w-3.5" />
            {:else}
              <ChevronDownOutline class="mr-1 h-3.5 w-3.5" />
            {/if}
            Statistics
          </button>
        {/if}
        <Select items={sortOptions} bind:value={sort} onchange={changeSort} aria-label="Sort videos" class="w-48 text-sm" />
      </div>
    </div>

    {#if statisticsExpanded}
      {#if statisticsError}
        <div
          id="channel-statistics-panel"
          class="mt-4 flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
          role="alert"
        >
          <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
          <span>{statisticsError}</span>
        </div>
      {:else if statistics}
        <section id="channel-statistics-panel" class="mt-4 rounded-2xl border border-base-300/80 bg-base-200/35 p-5" aria-labelledby="channel-statistics-title">
        <div class="flex flex-col gap-1 sm:flex-row sm:items-end sm:justify-between">
          <div>
            <h2 id="channel-statistics-title" class="text-base font-bold text-base-content">Channel statistics</h2>
            <p class="mt-1 text-sm text-base-content/50">
              {statistics.summary.downloadedCount.toLocaleString()} of {statistics.summary.availableCount.toLocaleString()} available items downloaded
            </p>
          </div>
          <p class="text-sm font-semibold text-primary">{percent(statistics.summary.downloadedPercent)} coverage</p>
        </div>

        <div class="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <article class="rounded-xl border border-base-300/80 bg-base-200/30 p-4">
            <p class="text-xs font-semibold uppercase tracking-wide text-base-content/50">Downloaded</p>
            <p class="mt-3 text-3xl font-bold text-base-content">{statistics.summary.downloadedCount.toLocaleString()}</p>
            <p class="mt-1 text-xs text-base-content/50">{formatDuration(statistics.summary.downloadedDurationSeconds) ?? 'no duration'} archived</p>
          </article>
          <article class="rounded-xl border border-base-300/80 bg-base-200/30 p-4">
            <p class="text-xs font-semibold uppercase tracking-wide text-base-content/50">Available</p>
            <p class="mt-3 text-3xl font-bold text-base-content">{statistics.summary.availableCount.toLocaleString()}</p>
            <p class="mt-1 text-xs text-base-content/50">{formatDuration(statistics.summary.totalDurationSeconds) ?? 'no duration'} discovered</p>
          </article>
          <article class="rounded-xl border border-base-300/80 bg-base-200/30 p-4">
            <p class="text-xs font-semibold uppercase tracking-wide text-base-content/50">Storage</p>
            <p class="mt-3 text-3xl font-bold text-base-content">{formatBytes(statistics.summary.totalBytes)}</p>
            <p class="mt-1 text-xs text-base-content/50">downloaded bytes</p>
          </article>
          <article class="rounded-xl border border-base-300/80 bg-base-200/30 p-4">
            <p class="text-xs font-semibold uppercase tracking-wide text-base-content/50">Last scan</p>
            <p class="mt-3 text-3xl font-bold text-base-content">{formatRelativeDate(statistics.summary.lastSuccessfulScanAt) ?? '-'}</p>
            <p class="mt-1 text-xs text-base-content/50">successful discovery scan</p>
          </article>
        </div>

        <div class="mt-5 grid gap-5 xl:grid-cols-3">
          <section class="rounded-xl border border-base-300/80 bg-base-200/25 p-4" aria-labelledby="channel-status-title">
            <h3 id="channel-status-title" class="text-sm font-bold text-base-content/90">Discovery status</h3>
            <div class="mt-4 grid gap-2">
              {#each [
                { label: 'Available', value: statistics.summary.availableCount },
                { label: 'Ignored', value: statistics.ignoredCount },
                { label: 'Unavailable', value: statistics.unavailableCount },
                { label: 'Removed', value: statistics.removedCount }
              ] as item (item.label)}
                {@const total = Math.max(statusTotal(statistics), 1)}
                <div>
                  <div class="flex justify-between gap-3 text-xs">
                    <span class="font-semibold text-base-content/80">{item.label}</span>
                    <span class="text-base-content/50">{item.value.toLocaleString()}</span>
                  </div>
                  <div class="mt-1 h-2 overflow-hidden rounded-full bg-base-300">
                    <div class="h-full rounded-full bg-primary" style={`width: ${Math.max(3, (item.value / total) * 100)}%`}></div>
                  </div>
                </div>
              {/each}
            </div>
          </section>

          <section class="rounded-xl border border-base-300/80 bg-base-200/25 p-4" aria-labelledby="channel-media-types-title">
            <h3 id="channel-media-types-title" class="text-sm font-bold text-base-content/90">Media types</h3>
            <div class="mt-4 space-y-3">
              {#each statistics.mediaTypes as item (item.type)}
                {@const maxCount = Math.max(...statistics.mediaTypes.map((type) => type.count), 1)}
                <div>
                  <div class="flex justify-between gap-3 text-xs">
                    <span class="font-semibold text-base-content/80">{item.type || 'Unknown'}</span>
                    <span class="text-base-content/50">{item.count.toLocaleString()} · {formatBytes(item.bytes)}</span>
                  </div>
                  <div class="mt-1 h-2 overflow-hidden rounded-full bg-base-300">
                    <div class="h-full rounded-full bg-success" style={`width: ${Math.max(3, (item.count / maxCount) * 100)}%`}></div>
                  </div>
                </div>
              {:else}
                <p class="text-sm text-base-content/50">No media type statistics yet.</p>
              {/each}
            </div>
          </section>

          <section class="rounded-xl border border-base-300/80 bg-base-200/25 p-4" aria-labelledby="channel-download-states-title">
            <h3 id="channel-download-states-title" class="text-sm font-bold text-base-content/90">Recent download states</h3>
            <div class="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-1">
              {#each statistics.recentDownloadStates as state (state.state)}
                <div class="rounded-lg border border-base-300/70 bg-base-200/45 p-3">
                  <p class="text-xs font-semibold uppercase tracking-wide text-base-content/50">{state.state}</p>
                  <p class="mt-2 text-xl font-bold text-base-content">{state.count.toLocaleString()}</p>
                </div>
              {:else}
                <p class="text-sm text-base-content/50">No recent download states yet.</p>
              {/each}
            </div>
          </section>
        </div>
      </section>
      {/if}
    {/if}

    {#if mediaError}
      <div
        class="mt-6 flex items-center gap-3 rounded-xl border border-error/30 bg-error/10 p-4 text-sm text-error"
        role="alert"
      >
        <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
        <span>{mediaError}</span>
      </div>
    {:else if mediaLoading}
      <div class="mt-16 flex justify-center">
        <span class="loading loading-spinner loading-md"></span>
      </div>
    {:else if items.length === 0}
      <div class="mt-10 rounded-2xl border border-base-300/80 bg-base-200/40 p-10 text-center">
        <RectangleListOutline class="mx-auto h-10 w-10 text-base-content/30" />
        <p class="mt-4 text-sm font-semibold text-base-content/80">No videos archived yet</p>
        <p class="mt-1 text-sm text-base-content/50">
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
                class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-base-100 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
              >
                <PlaySolid class="ml-0.5 h-5 w-5" />
              </span>
            </a>
            <div class="mt-3 min-w-0 px-1">
              <h3 class="line-clamp-2 text-sm font-semibold leading-snug text-base-content/90">
                {card.title}
              </h3>
              {#if cardMeta(card)}
                <p class="mt-1 truncate text-xs text-base-content/40">{cardMeta(card)}</p>
              {/if}
            </div>
          </article>
        {/each}
      </div>

      <div class="mt-8 flex items-center justify-between border-t border-base-300/70 pt-5">
        <p class="text-xs text-base-content/40">
          Page {mediaPage} of {totalPages}
        </p>
        <div class="flex gap-2">
          <button class="btn btn-sm btn-neutral text-xs" disabled={mediaPage <= 1 || mediaLoading} onclick={() => loadMedia(accountId, mediaPage - 1)}>
            <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
            Previous
          </button>
          <button class="btn btn-sm btn-neutral text-xs" disabled={!hasMore || mediaLoading} onclick={() => loadMedia(accountId, mediaPage + 1)}>
            Next
            <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
          </button>
        </div>
      </div>
    {/if}
  </section>
{/if}
