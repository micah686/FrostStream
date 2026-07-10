<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { browser } from '$app/environment';
  import { Button, Select, Spinner } from 'flowbite-svelte';
  import {
    ArrowsRepeatOutline,
    CheckCircleOutline,
    CheckCircleSolid,
    ChevronDownOutline,
    ChevronUpOutline,
    DotsHorizontalOutline,
    EditOutline,
    ExclamationCircleOutline,
    ForwardStepOutline,
    HeartOutline,
    HeartSolid,
    SearchOutline,
    ShareNodesOutline,
    ShuffleOutline,
    ThumbsDownOutline,
    ThumbsUpOutline
  } from 'flowbite-svelte-icons';
  import VideoJs10Player, { type TextTrackSource } from '$lib/components/players/VideoJs10Player.svelte';
  import SveltePlayer from '$lib/components/players/SveltePlayer.svelte';
  import CastDropdown from '$lib/components/players/CastDropdown.svelte';
  import SaveToPlaylistButton from '$lib/components/SaveToPlaylistButton.svelte';
  import PlaylistPanel from '$lib/components/PlaylistPanel.svelte';
  import TargetNotePanel from '$lib/components/TargetNotePanel.svelte';
  import {
    getWatchState,
    markUnwatched,
    markWatched,
    updateWatchState,
    type WatchState
  } from '$lib/api/watchState';
  import { getLikeState, likeMedia, unlikeMedia, type MediaLikeState } from '$lib/api/mediaLikes';
  import { getMetadataVersions, type MetadataVersion } from '$lib/api/metadata';
  import {
    accentFor,
    formatCount,
    formatDuration,
    formatRelativeDate,
    formatViews,
    initialsFor
  } from '$lib/media';

  interface Series {
    seriesName: string;
    seasonCount?: number | null;
    seasonNumber: number;
    seasonName?: string | null;
    episodeNumber: number;
    episodeName: string;
  }

  interface Music {
    albumTitle: string;
    albumType?: string | null;
    discNumber?: number | null;
    releaseYear?: number | null;
    trackTitle: string;
    trackNumber: number;
    composer?: string | null;
  }

  interface CaptionLanguage {
    languageCode: string;
    captionType: string;
    name?: string | null;
  }

  interface Detail {
    mediaGuid: string;
    title: string;
    description?: string | null;
    thumbnailStoragePath?: string | null;
    durationSeconds?: number | null;
    releaseDate?: string | null;
    viewCount?: number | null;
    likeCount?: number | null;
    dislikeCount?: number | null;
    averageRating?: number | null;
    commentCount?: number | null;
    ageLimit?: number | null;
    wasLive: boolean;
    availability?: string | null;
    location?: string | null;
    webpageUrl?: string | null;
    externalMediaId?: string | null;
    metadataScrapedAt: string;
    account: {
      accountId: number;
      accountName: string;
      accountHandle: string;
      followerCount?: number | null;
      isVerified: boolean;
    };
    tags: string[];
    categories: string[];
    genres: string[];
    cast: string[];
    artists: string[];
    albumArtists: string[];
    series?: Series | null;
    music?: Music | null;
    captionLanguages: CaptionLanguage[];
    userNote?: string | null;
  }

  interface TechnicalStream {
    streamType: string;
    isPrimary: boolean;
    codecName: string;
    codecLongName: string;
    bitRate: number;
    bitDepth?: number | null;
    durationTicks: number;
    language?: string | null;
    video?: {
      width: number;
      height: number;
      avgFrameRate: number;
      hdrType: string;
      colorSpace: string;
      profile: string;
    } | null;
    audio?: {
      channels: number;
      channelLayout: string;
      sampleRateHz: number;
      profile: string;
    } | null;
  }

  interface Technical {
    mediaGuid: string;
    durationTicks: number;
    format?: {
      durationTicks: number;
      startTimeTicks: number;
      formatLongNames: string;
      streamCount: number;
      bitRate: number;
    } | null;
    streams: TechnicalStream[];
    chapters: { title: string; startTicks: number; endTicks: number }[];
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
    thumbnailStoragePath?: string | null;
    durationSeconds?: number | null;
    releaseDate?: string | null;
    viewCount?: number | null;
    account: { accountName: string };
  }

  interface MediaVersionOption {
    value: string;
    name: string;
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
  let metaTab = $state<'details' | 'technical'>('details');
  let technical = $state<Technical | null>(null);
  let technicalError = $state<string | null>(null);
  let technicalRequested = $state(false);
  let watchState = $state<WatchState | null>(null);
  let watchStateLoaded = $state(false);
  let watchedBusy = $state(false);
  let likeState = $state<MediaLikeState | null>(null);
  let likeStateLoaded = $state(false);
  let likeBusy = $state(false);
  // After a manual "mark unwatched", don't let the 95% rule silently re-complete
  // the video during this visit; a natural end still marks it watched.
  let suppressAutoComplete = false;
  let lastProgressSentAt = 0;
  let lastSentPosition = -1;
  // Live local playback position for the server cast menu's "start from current position".
  let livePosition = $state(0);
  let moreMenuOpen = $state(false);
  let noteMenuOpen = $state(false);
  let moreMenuContainer = $state<HTMLDivElement | null>(null);
  let selectedVersion = $state('');
  let mediaVersionOptions = $state<MediaVersionOption[]>([{ value: '', name: 'Latest version' }]);
  let versionsLoading = $state(false);
  let streamChecking = $state(false);
  let streamError = $state<string | null>(null);
  let streamCheckSeq = 0;

  // Playback modes persist across videos and sessions. Repeat wins over autoplay:
  // a looping video never fires "ended", so autoplay simply never triggers.
  const playbackModesKey = 'froststream:playback-modes';
  let autoplayEnabled = $state(false);
  let repeatEnabled = $state(false);
  let shuffleEnabled = $state(false);
  // Ordered guids of the active playlist (null = entry not downloaded yet), reported by PlaylistPanel.
  let playlistGuids = $state<(string | null)[]>([]);

  if (browser) {
    try {
      const saved = JSON.parse(localStorage.getItem(playbackModesKey) ?? '{}') as Record<string, unknown>;
      autoplayEnabled = saved.autoplay === true;
      repeatEnabled = saved.repeat === true;
      shuffleEnabled = saved.shuffle === true;
    } catch {
      // Corrupt prefs fall back to everything off.
    }
  }

  $effect(() => {
    const prefs = JSON.stringify({
      autoplay: autoplayEnabled,
      repeat: repeatEnabled,
      shuffle: shuffleEnabled
    });
    if (browser) {
      localStorage.setItem(playbackModesKey, prefs);
    }
  });

  const playbackModes = $derived([
    {
      id: 'autoplay',
      label: 'Autoplay',
      title: 'Autoplay — play the next video when this one ends',
      icon: ForwardStepOutline,
      active: autoplayEnabled,
      toggle: () => (autoplayEnabled = !autoplayEnabled)
    },
    {
      id: 'repeat',
      label: 'Repeat',
      title: 'Repeat — keep replaying this video',
      icon: ArrowsRepeatOutline,
      active: repeatEnabled,
      toggle: () => (repeatEnabled = !repeatEnabled)
    },
    {
      id: 'shuffle',
      label: 'Shuffle',
      title: 'Shuffle — autoplay picks a random video instead of the next one',
      icon: ShuffleOutline,
      active: shuffleEnabled,
      toggle: () => (shuffleEnabled = !shuffleEnabled)
    }
  ]);

  $effect(() => {
    if (!moreMenuOpen) {
      noteMenuOpen = false;
      return;
    }
    const onPointerDown = (event: PointerEvent) => {
      if (moreMenuContainer && event.target instanceof Node && !moreMenuContainer.contains(event.target)) {
        moreMenuOpen = false;
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        moreMenuOpen = false;
      }
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  });

  const mediaGuid = $derived(page.params.mediaGuid ?? '');
  // Playlist context: ?ulist= plays through a user playlist, ?list= through a
  // platform (downloaded provider) playlist. Only one panel is shown; ulist wins.
  const userListId = $derived(page.url.searchParams.get('ulist'));
  const platformListId = $derived(page.url.searchParams.get('list'));
  const streamUrl = $derived.by(() => {
    const query = selectedVersion ? `?version=${encodeURIComponent(selectedVersion)}` : '';
    return `/api/media/watch/${mediaGuid}${query}`;
  });
  const watched = $derived(watchState?.completed === true);
  const liked = $derived(likeState?.liked === true);
  // ?t= wins over the saved position; positions within the first 5s or last 5% are
  // treated as "start over" so a finished video doesn't resume at the outro.
  const startAtParam = $derived.by(() => {
    const raw = page.url.searchParams.get('t');
    const value = raw === null ? NaN : Number(raw);
    return Number.isFinite(value) && value > 0 ? value : null;
  });
  const resumeTime = $derived.by(() => {
    if (startAtParam !== null) {
      return startAtParam;
    }
    const state = watchState;
    if (!state || state.completed || !state.positionSeconds || state.positionSeconds < 5) {
      return null;
    }
    if (state.durationSeconds && state.positionSeconds >= state.durationSeconds * 0.95) {
      return null;
    }
    return state.positionSeconds;
  });
  const posterUrl = $derived(detail?.thumbnailStoragePath ? `/api/media/watch/${mediaGuid}/thumbnail` : null);
  const captionTracks = $derived.by((): TextTrackSource[] => {
    const current = detail;
    if (!current) {
      return [];
    }
    return current.captionLanguages.map((caption) => ({
      src: `/api/media/watch/${current.mediaGuid}/captions/${encodeURIComponent(caption.languageCode)}?captionType=${encodeURIComponent(caption.captionType)}`,
      srclang: caption.languageCode,
      label: captionSummary(caption),
      kind: caption.captionType === 'automatic_captions' ? 'captions' : 'subtitles'
    }));
  });

  $effect(() => {
    if (mediaGuid) {
      void loadAll(mediaGuid);
    }
  });

  $effect(() => {
    const guid = mediaGuid;
    const url = streamUrl;
    if (!guid || loadError) {
      return;
    }
    void checkStreamAvailability(guid, url);
  });

  async function loadAll(guid: string) {
    detail = null;
    loadError = null;
    comments = [];
    commentTotal = 0;
    commentsHaveMore = false;
    commentPage = 1;
    descriptionExpanded = false;
    metaTab = 'details';
    technical = null;
    technicalError = null;
    technicalRequested = false;
    watchState = null;
    watchStateLoaded = false;
    likeState = null;
    likeStateLoaded = false;
    likeBusy = false;
    suppressAutoComplete = false;
    lastProgressSentAt = 0;
    lastSentPosition = -1;
    selectedVersion = '';
    mediaVersionOptions = [{ value: '', name: 'Latest version' }];
    versionsLoading = false;
    streamChecking = false;
    streamError = null;

    await Promise.all([
      loadDetail(guid),
      loadComments(guid, 1),
      loadUpNext(guid),
      loadWatchState(guid),
      loadLikeState(guid),
      loadVersions(guid)
    ]);
  }

  async function checkStreamAvailability(guid: string, url: string) {
    const seq = ++streamCheckSeq;
    streamChecking = true;
    streamError = null;
    try {
      const response = await fetch(url, { method: 'HEAD', cache: 'no-store' });
      if (seq !== streamCheckSeq || guid !== mediaGuid || url !== streamUrl) {
        return;
      }
      if (response.ok) {
        return;
      }
      if (response.status === 404) {
        streamError =
          'The archived media file is missing from storage. If this item was downloaded before the storage mount was fixed, queue it again with Force download enabled.';
        return;
      }
      if (response.status === 401) {
        streamError = 'Your session has expired. Log in again before playback.';
        return;
      }
      if (response.status === 403) {
        streamError = 'You do not have permission to play this media.';
        return;
      }
      streamError = `The media stream is not available right now (status ${response.status}).`;
    } catch (err) {
      if (seq === streamCheckSeq && guid === mediaGuid && url === streamUrl) {
        streamError = err instanceof Error ? err.message : 'Could not check media stream availability.';
      }
    } finally {
      if (seq === streamCheckSeq && guid === mediaGuid && url === streamUrl) {
        streamChecking = false;
      }
    }
  }

  async function loadVersions(guid: string) {
    versionsLoading = true;
    try {
      const response = await getMetadataVersions(guid);
      const versions = response.versions;
      mediaVersionOptions = [
        { value: '', name: 'Latest version' },
        ...versions.map((version: MetadataVersion) => ({
          value: String(version.versionNum),
          name: `Version ${version.versionNum}`
        }))
      ];
    } catch {
      mediaVersionOptions = [{ value: '', name: 'Latest version' }];
    } finally {
      versionsLoading = false;
    }
  }

  async function loadWatchState(guid: string) {
    try {
      watchState = await getWatchState(guid);
    } catch {
      // Playback works without a saved state; resume and the toggle just start blank.
      watchState = null;
    } finally {
      watchStateLoaded = true;
    }
  }

  async function loadLikeState(guid: string) {
    try {
      likeState = await getLikeState(guid);
    } catch {
      likeState = null;
    } finally {
      likeStateLoaded = true;
    }
  }

  function handlePlaybackProgress(positionSeconds: number, durationSeconds: number | null) {
    livePosition = positionSeconds;
    const now = Date.now();
    if (now - lastProgressSentAt < 10_000 || Math.abs(positionSeconds - lastSentPosition) < 2) {
      return;
    }
    lastProgressSentAt = now;
    lastSentPosition = positionSeconds;

    const nearEnd = durationSeconds !== null && positionSeconds >= durationSeconds * 0.95;
    const completed = watched || (nearEnd && !suppressAutoComplete);
    const guid = mediaGuid;
    updateWatchState(guid, { positionSeconds, durationSeconds, completed })
      .then((state) => {
        if (guid === mediaGuid) {
          watchState = state;
        }
      })
      .catch(() => {
        // Progress reporting is best-effort; the next tick retries.
      });
  }

  function handlePlaybackEnded() {
    suppressAutoComplete = false;
    const guid = mediaGuid;
    const duration = watchState?.durationSeconds ?? detail?.durationSeconds ?? null;
    updateWatchState(guid, { positionSeconds: duration, durationSeconds: duration, completed: true })
      .then((state) => {
        if (guid === mediaGuid) {
          watchState = state;
        }
      })
      .catch(() => {});
    void advanceAfterEnd(guid);
  }

  async function advanceAfterEnd(current: string) {
    // Repeat is handled by the video element's loop attribute; "ended" only fires
    // here if repeat was switched off, in which case autoplay may take over.
    if (repeatEnabled || !autoplayEnabled) {
      return;
    }
    const target = await pickNextTarget(current);
    if (target && current === mediaGuid) {
      await goto(target);
    }
  }

  async function pickNextTarget(current: string): Promise<string | null> {
    if (userListId || platformListId) {
      const listQuery = userListId
        ? `?ulist=${encodeURIComponent(userListId)}`
        : `?list=${encodeURIComponent(platformListId ?? '')}`;
      if (shuffleEnabled) {
        const pool = playlistGuids.filter((guid): guid is string => guid !== null && guid !== current);
        if (pool.length === 0) {
          return null;
        }
        return `/watch/${pool[Math.floor(Math.random() * pool.length)]}${listQuery}`;
      }
      const index = playlistGuids.indexOf(current);
      const next = playlistGuids.slice(index + 1).find((guid) => guid !== null);
      return next ? `/watch/${next}${listQuery}` : null;
    }

    if (shuffleEnabled) {
      try {
        const response = await fetch(`/api/metadata/random?exclude=${encodeURIComponent(current)}`);
        if (!response.ok) {
          return null;
        }
        const data = (await response.json()) as { mediaGuid?: string };
        return data.mediaGuid && data.mediaGuid !== current ? `/watch/${data.mediaGuid}` : null;
      } catch {
        return null;
      }
    }
    return upNext.length > 0 ? `/watch/${upNext[0].mediaGuid}` : null;
  }

  async function toggleWatched() {
    if (watchedBusy || !watchStateLoaded) {
      return;
    }
    watchedBusy = true;
    const guid = mediaGuid;
    try {
      const state = watched ? await markUnwatched(guid) : await markWatched(guid);
      suppressAutoComplete = !state.completed;
      if (guid === mediaGuid) {
        watchState = state;
      }
    } catch {
      // Leave the current toggle state; the user can retry.
    } finally {
      watchedBusy = false;
    }
  }

  async function toggleLike() {
    if (likeBusy || !likeStateLoaded) {
      return;
    }
    likeBusy = true;
    const guid = mediaGuid;
    try {
      const state = liked ? await unlikeMedia(guid) : await likeMedia(guid);
      if (guid === mediaGuid) {
        likeState = state;
      }
    } catch {
      // Leave the current toggle state; the user can retry.
    } finally {
      likeBusy = false;
    }
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

  function openTechnicalTab() {
    metaTab = 'technical';
    if (!technicalRequested) {
      technicalRequested = true;
      void loadTechnical(mediaGuid);
    }
  }

  async function loadTechnical(guid: string) {
    try {
      const response = await fetch(`/api/metadata/${guid}/technical`);
      if (!response.ok) {
        technicalError =
          response.status === 404
            ? 'No technical metadata was archived for this video.'
            : `Could not load technical metadata (status ${response.status}).`;
        return;
      }
      technical = (await response.json()) as Technical;
    } catch (err) {
      technicalError = err instanceof Error ? err.message : 'Could not load technical metadata.';
    }
  }

  function formatFullDate(iso: string | null | undefined): string | null {
    if (!iso) {
      return null;
    }
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    return date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  function formatTicks(ticks: number | null | undefined): string | null {
    return ticks && ticks > 0 ? formatDuration(ticks / 10_000_000) : null;
  }

  function formatBitRate(bps: number | null | undefined): string | null {
    if (!bps || bps <= 0) {
      return null;
    }
    return bps >= 1_000_000 ? `${(bps / 1_000_000).toFixed(1)} Mbps` : `${Math.round(bps / 1000)} kbps`;
  }

  function seriesSummary(series: Series): string {
    const season = series.seasonName
      ? `${series.seasonName} (Season ${series.seasonNumber})`
      : `Season ${series.seasonNumber}`;
    return `${series.seriesName} · ${season} · Episode ${series.episodeNumber}: ${series.episodeName}`;
  }

  function musicSummary(music: Music): string {
    return [
      `${music.trackTitle} (track ${music.trackNumber})`,
      music.albumTitle,
      music.composer ? `composed by ${music.composer}` : null,
      music.releaseYear ? String(music.releaseYear) : null
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function captionSummary(caption: CaptionLanguage): string {
    const label = caption.name || caption.languageCode;
    return caption.captionType === 'automatic_captions' ? `${label} (auto)` : label;
  }

  function streamSummary(stream: TechnicalStream): string {
    const parts: (string | null)[] = [];
    if (stream.video) {
      parts.push(`${stream.video.width}×${stream.video.height}`);
      if (stream.video.avgFrameRate > 0) {
        parts.push(`${Math.round(stream.video.avgFrameRate * 100) / 100} fps`);
      }
      if (stream.video.hdrType && stream.video.hdrType.toLowerCase() !== 'none') {
        parts.push(stream.video.hdrType);
      }
      if (stream.video.profile) {
        parts.push(stream.video.profile);
      }
    }
    if (stream.audio) {
      parts.push(stream.audio.channelLayout || `${stream.audio.channels}ch`);
      if (stream.audio.sampleRateHz > 0) {
        parts.push(`${(stream.audio.sampleRateHz / 1000).toFixed(1)} kHz`);
      }
      if (stream.audio.profile) {
        parts.push(stream.audio.profile);
      }
    }
    parts.push(formatBitRate(stream.bitRate));
    if (stream.bitDepth) {
      parts.push(`${stream.bitDepth}-bit`);
    }
    if (stream.language) {
      parts.push(stream.language);
    }
    return parts.filter(Boolean).join(' · ');
  }

  const detailRows = $derived.by(() => {
    if (!detail) {
      return [] as { label: string; value: string; href?: string }[];
    }
    const rows: { label: string; value: string; href?: string }[] = [];
    const push = (label: string, value: string | null | undefined, href?: string) => {
      if (value) {
        rows.push(href ? { label, value, href } : { label, value });
      }
    };
    push('Released', formatFullDate(detail.releaseDate));
    push('Duration', formatDuration(detail.durationSeconds));
    push('Views', detail.viewCount?.toLocaleString());
    push('Likes', detail.likeCount?.toLocaleString());
    push('Dislikes', detail.dislikeCount?.toLocaleString());
    push('Rating', detail.averageRating != null ? detail.averageRating.toFixed(1) : null);
    push('Comments', detail.commentCount?.toLocaleString());
    push('Age limit', detail.ageLimit ? `${detail.ageLimit}+` : null);
    push('Availability', detail.availability);
    push('Live broadcast', detail.wasLive ? 'Yes' : null);
    push('Location', detail.location);
    push('Source', detail.webpageUrl, detail.webpageUrl ?? undefined);
    push('External ID', detail.externalMediaId);
    push(
      'Captions',
      detail.captionLanguages.length > 0 ? detail.captionLanguages.map(captionSummary).join(', ') : null
    );
    push('Metadata archived', formatFullDate(detail.metadataScrapedAt));
    return rows;
  });

  function upNextMeta(card: UpNextCard): string {
    return [formatCount(card.viewCount), formatRelativeDate(card.releaseDate)]
      .filter(Boolean)
      .join(' · ');
  }

  function thumbnailUrl(card: UpNextCard): string | null {
    return card.thumbnailStoragePath ? `/api/media/watch/${card.mediaGuid}/thumbnail` : null;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<svelte:head>
  <title>{detail ? `${detail.title} · FrostStream` : 'Watch · FrostStream'}</title>
</svelte:head>

<div class="grid gap-8 xl:grid-cols-[minmax(0,1fr)_360px]">
  <section class="min-w-0" aria-label="Video player">
    <div class="mb-3 flex flex-col gap-2 sm:flex-row sm:items-center">
      <div class="flex flex-1 gap-1 rounded-xl border border-slate-800/70 bg-slate-900/40 p-1" role="tablist" aria-label="Player implementation">
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
      <div class="flex items-center gap-2">
        <div
          class="flex gap-1 rounded-xl border border-slate-800/70 bg-slate-900/40 p-1"
          role="group"
          aria-label="Playback modes"
        >
          {#each playbackModes as mode (mode.id)}
            <button
              type="button"
              onclick={mode.toggle}
              aria-pressed={mode.active}
              aria-label={mode.label}
              title={mode.title}
              class={[
                'grid h-8 w-9 place-items-center rounded-lg transition',
                mode.active
                  ? 'bg-blue-500/15 text-blue-400'
                  : 'text-slate-500 hover:bg-slate-800/70 hover:text-slate-300'
              ]}
            >
              <mode.icon class="h-4 w-4" />
            </button>
          {/each}
        </div>
        {#if versionsLoading}
          <span class="flex items-center gap-2 rounded-xl border border-slate-800/70 bg-slate-900/40 px-3 py-2 text-xs text-slate-500">
            <Spinner size="4" />
            Versions
          </span>
        {:else}
          <Select
            items={mediaVersionOptions}
            bind:value={selectedVersion}
            aria-label="Media version"
            class="w-full border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500! sm:w-44!"
          />
        {/if}
      </div>
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
        {#if !watchStateLoaded || streamChecking}
          <div class="grid h-full w-full place-items-center">
            <Spinner size="8" />
          </div>
        {:else if streamError}
          <div class="flex h-full items-center justify-center p-6">
            <div class="max-w-xl rounded-2xl border border-amber-500/30 bg-amber-500/10 p-5 text-center text-amber-100">
              <ExclamationCircleOutline class="mx-auto h-8 w-8 text-amber-300" />
              <h2 class="mt-3 text-base font-bold text-amber-50">Playback file unavailable</h2>
              <p class="mt-2 text-sm leading-6 text-amber-100/85">{streamError}</p>
            </div>
          </div>
        {:else}
          {#key `${mediaGuid}:${playerTab}:${selectedVersion}`}
            {#if playerTab === 'videojs'}
              <VideoJs10Player
                src={streamUrl}
                poster={posterUrl}
                tracks={captionTracks}
                startTime={resumeTime}
                loop={repeatEnabled}
                autoplay={autoplayEnabled}
                onProgress={handlePlaybackProgress}
                onEnded={handlePlaybackEnded}
              />
            {:else}
              <SveltePlayer
                src={streamUrl}
                poster={posterUrl}
                startTime={resumeTime}
                loop={repeatEnabled}
                autoplay={autoplayEnabled}
                onProgress={handlePlaybackProgress}
                onEnded={handlePlaybackEnded}
              />
            {/if}
          {/key}
        {/if}
      </div>
    {/if}

    {#if detail}
      <h1 class="mt-4 text-xl font-bold tracking-tight text-white sm:text-2xl">{detail.title}</h1>

      <div class="mt-3 flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-3">
          <a
            href={`/channel/${detail.account.accountId}`}
            aria-label={`Open ${detail.account.accountName}'s channel`}
            class={`relative grid h-10 w-10 shrink-0 place-items-center overflow-hidden rounded-full bg-gradient-to-br ${accentFor(detail.account.accountName)} text-xs font-bold text-white`}
          >
            {initialsFor(detail.account.accountName)}
            <img
              src={`/api/media/watch/accounts/${detail.account.accountId}/avatar`}
              alt=""
              loading="lazy"
              decoding="async"
              class="absolute inset-0 h-full w-full object-cover"
              onerror={hideBrokenImage}
            />
          </a>
          <div class="min-w-0">
            <p class="flex items-center gap-1 text-sm font-semibold text-slate-200">
              <a href={`/channel/${detail.account.accountId}`} class="hover:text-white">
                {detail.account.accountName}
              </a>
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
        </div>

        <div class="flex items-center gap-2">
          <button
            type="button"
            onclick={toggleLike}
            disabled={!likeStateLoaded || likeBusy}
            aria-pressed={liked}
            aria-label={liked ? 'Remove from favorites' : 'Add to favorites'}
            title={liked ? 'Remove from favorites' : 'Add to favorites'}
            class={[
              'grid h-9 w-9 place-items-center rounded-lg border transition disabled:opacity-60',
              liked
                ? 'border-rose-500/50 bg-rose-500/10 text-rose-300 hover:bg-rose-500/20'
                : 'border-slate-800 bg-slate-900/70 text-slate-400 hover:bg-slate-800 hover:text-slate-200'
            ]}
          >
            {#if liked}
              <HeartSolid class="h-4 w-4" />
            {:else}
              <HeartOutline class="h-4 w-4" />
            {/if}
          </button>
          <button
            type="button"
            onclick={toggleWatched}
            disabled={!watchStateLoaded || watchedBusy}
            aria-pressed={watched}
            title={watched ? 'Mark as unwatched' : 'Mark as watched'}
            class={[
              'flex items-center gap-1.5 rounded-lg border px-4 py-2 text-xs font-semibold transition disabled:opacity-60',
              watched
                ? 'border-blue-500/50 bg-blue-500/10 text-blue-300 hover:bg-blue-500/20'
                : 'border-slate-800 bg-slate-900/70 text-slate-300 hover:bg-slate-800'
            ]}
          >
            {#if watched}
              <CheckCircleSolid class="h-4 w-4" />
              Watched
            {:else}
              <CheckCircleOutline class="h-4 w-4" />
              Mark watched
            {/if}
          </button>
          <CastDropdown
            {mediaGuid}
            title={detail.title}
            posterUrl={posterUrl}
            captionLanguages={detail.captionLanguages}
            position={livePosition}
          />
          <Button
            color="dark"
            class="border-slate-800! bg-slate-900/70! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
          >
            <ShareNodesOutline class="mr-1.5 h-4 w-4" />
            Share
          </Button>
          <SaveToPlaylistButton {mediaGuid} />
          <div class="relative" bind:this={moreMenuContainer}>
            <button
              type="button"
              aria-label="More actions"
              aria-haspopup="menu"
              aria-expanded={moreMenuOpen}
              onclick={() => (moreMenuOpen = !moreMenuOpen)}
              class={[
                'grid h-9 w-9 place-items-center rounded-lg border border-slate-800 text-slate-400 transition hover:bg-slate-800',
                moreMenuOpen ? 'bg-slate-800' : 'bg-slate-900/70'
              ]}
            >
              <DotsHorizontalOutline class="h-4 w-4" />
            </button>

            {#if moreMenuOpen}
              <div
                class="absolute right-0 top-full z-40 mt-2 w-80 rounded-xl border border-slate-700/80 bg-[#151a26] p-1.5 shadow-2xl shadow-black/50"
                role="menu"
                aria-label="More actions"
              >
                <a
                  role="menuitem"
                  href={`/search?similar=${mediaGuid}`}
                  onclick={() => (moreMenuOpen = false)}
                  class="flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium text-slate-200 transition hover:bg-slate-800/70"
                >
                  <SearchOutline class="h-4 w-4 text-slate-500" />
                  Find similar
                </a>
                <button
                  type="button"
                  role="menuitem"
                  onclick={() => (noteMenuOpen = !noteMenuOpen)}
                  class="flex w-full items-center justify-between gap-2.5 rounded-lg px-3 py-2 text-left text-sm font-medium text-slate-200 transition hover:bg-slate-800/70"
                >
                  <span class="flex items-center gap-2.5">
                    <EditOutline class={['h-4 w-4', detail.userNote ? 'text-blue-400' : 'text-slate-500']} />
                    Note
                  </span>
                  {#if noteMenuOpen}
                    <ChevronUpOutline class="h-3.5 w-3.5 text-slate-500" />
                  {:else}
                    <ChevronDownOutline class="h-3.5 w-3.5 text-slate-500" />
                  {/if}
                </button>
                {#if noteMenuOpen}
                  <div class="mt-1 rounded-lg border border-slate-700/70 bg-slate-950/45 p-3">
                    <TargetNotePanel
                      targetType="video"
                      targetId={mediaGuid}
                      targetLabel="Video"
                      initialNote={detail.userNote ?? null}
                      embedded
                      initialOpen
                      onChange={(note) => {
                        if (detail) {
                          detail = { ...detail, userNote: note };
                        }
                      }}
                    />
                  </div>
                {/if}
              </div>
            {/if}
          </div>
        </div>
      </div>

      <div class="mt-4 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm font-semibold text-slate-300">
          {#if formatViews(detail.viewCount)}<span>{formatViews(detail.viewCount)}</span>{/if}
          {#if formatRelativeDate(detail.releaseDate)}<span class="text-slate-500">·</span><span>{formatRelativeDate(detail.releaseDate)}</span>{/if}
          {#if detail.likeCount != null}
            <span class="inline-flex items-center gap-1 text-slate-400" title="Provider likes at download time">
              <ThumbsUpOutline class="h-3.5 w-3.5" />
              {formatCount(detail.likeCount) ?? detail.likeCount.toLocaleString()}
            </span>
          {/if}
          {#if detail.dislikeCount != null}
            <span class="inline-flex items-center gap-1 text-slate-500" title="Provider dislikes at download time">
              <ThumbsDownOutline class="h-3.5 w-3.5" />
              {formatCount(detail.dislikeCount) ?? detail.dislikeCount.toLocaleString()}
            </span>
          {/if}
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

      <section class="mt-4 rounded-2xl border border-slate-800/80 bg-slate-900/40" aria-label="Media metadata">
        <div class="flex gap-1 border-b border-slate-800/80 p-2" role="tablist" aria-label="Metadata sections">
          <button
            type="button"
            role="tab"
            aria-selected={metaTab === 'details'}
            onclick={() => (metaTab = 'details')}
            class={[
              'rounded-lg px-4 py-2 text-xs font-semibold transition',
              metaTab === 'details'
                ? 'bg-blue-500/15 text-blue-400'
                : 'text-slate-500 hover:bg-slate-800/70 hover:text-slate-300'
            ]}
          >
            Details
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={metaTab === 'technical'}
            onclick={openTechnicalTab}
            class={[
              'rounded-lg px-4 py-2 text-xs font-semibold transition',
              metaTab === 'technical'
                ? 'bg-blue-500/15 text-blue-400'
                : 'text-slate-500 hover:bg-slate-800/70 hover:text-slate-300'
            ]}
          >
            Technical
          </button>
        </div>

        {#if metaTab === 'details'}
          <div class="space-y-5 p-5">
            {#if detailRows.length > 0}
              <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
                {#each detailRows as row (row.label)}
                  <div class="flex min-w-0 gap-2">
                    <dt class="w-32 shrink-0 text-slate-500">{row.label}</dt>
                    <dd class="min-w-0 flex-1 truncate text-slate-300">
                      {#if row.href}
                        <a
                          href={row.href}
                          target="_blank"
                          rel="noopener noreferrer"
                          class="text-blue-400 hover:underline"
                        >
                          {row.value}
                        </a>
                      {:else}
                        {row.value}
                      {/if}
                    </dd>
                  </div>
                {/each}
              </dl>
            {/if}

            {#if detail.series}
              <div>
                <h3 class="text-xs font-bold uppercase tracking-[0.08em] text-slate-500">Series</h3>
                <p class="mt-1.5 text-sm text-slate-300">{seriesSummary(detail.series)}</p>
              </div>
            {/if}

            {#if detail.music}
              <div>
                <h3 class="text-xs font-bold uppercase tracking-[0.08em] text-slate-500">Music</h3>
                <p class="mt-1.5 text-sm text-slate-300">{musicSummary(detail.music)}</p>
              </div>
            {/if}

            {#each [
              { label: 'Tags', values: detail.tags, prefix: '#', chip: 'bg-slate-800/80 text-slate-400' },
              { label: 'Categories', values: detail.categories, prefix: '', chip: 'bg-blue-500/10 text-blue-300' },
              { label: 'Genres', values: detail.genres, prefix: '', chip: 'bg-purple-500/10 text-purple-300' },
              { label: 'Cast', values: detail.cast, prefix: '', chip: 'bg-slate-800/80 text-slate-400' },
              { label: 'Artists', values: detail.artists, prefix: '', chip: 'bg-slate-800/80 text-slate-400' },
              { label: 'Album artists', values: detail.albumArtists, prefix: '', chip: 'bg-slate-800/80 text-slate-400' }
            ] as group (group.label)}
              {#if group.values.length > 0}
                <div>
                  <h3 class="text-xs font-bold uppercase tracking-[0.08em] text-slate-500">{group.label}</h3>
                  <div class="mt-1.5 flex flex-wrap gap-1.5">
                    {#each group.values as value}
                      <span class={`rounded-full px-2.5 py-0.5 text-xs font-medium ${group.chip}`}>
                        {group.prefix}{value}
                      </span>
                    {/each}
                  </div>
                </div>
              {/if}
            {/each}

          </div>
        {:else}
          <div class="space-y-5 p-5">
            {#if technicalError}
              <p class="text-sm text-slate-500">{technicalError}</p>
            {:else if !technical}
              <div class="flex justify-center py-4">
                <Spinner size="5" />
              </div>
            {:else}
              {#if technical.format}
                <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
                  <div class="flex gap-2">
                    <dt class="w-32 shrink-0 text-slate-500">Container</dt>
                    <dd class="min-w-0 text-slate-300">{technical.format.formatLongNames}</dd>
                  </div>
                  {#if formatTicks(technical.format.durationTicks)}
                    <div class="flex gap-2">
                      <dt class="w-32 shrink-0 text-slate-500">Duration</dt>
                      <dd class="text-slate-300">{formatTicks(technical.format.durationTicks)}</dd>
                    </div>
                  {/if}
                  {#if formatBitRate(technical.format.bitRate)}
                    <div class="flex gap-2">
                      <dt class="w-32 shrink-0 text-slate-500">Overall bitrate</dt>
                      <dd class="text-slate-300">{formatBitRate(technical.format.bitRate)}</dd>
                    </div>
                  {/if}
                  <div class="flex gap-2">
                    <dt class="w-32 shrink-0 text-slate-500">Streams</dt>
                    <dd class="text-slate-300">{technical.format.streamCount}</dd>
                  </div>
                </dl>
              {/if}

              {#if technical.streams.length > 0}
                <div>
                  <h3 class="text-xs font-bold uppercase tracking-[0.08em] text-slate-500">Streams</h3>
                  <ul class="mt-2 space-y-2">
                    {#each technical.streams as stream}
                      <li class="rounded-xl border border-slate-800/80 bg-slate-950/40 px-4 py-3">
                        <p class="flex flex-wrap items-center gap-2 text-xs">
                          <span class="rounded-full bg-slate-800 px-2 py-0.5 font-semibold uppercase tracking-wide text-slate-300">
                            {stream.streamType}
                          </span>
                          {#if stream.isPrimary}
                            <span class="rounded-full bg-blue-500/15 px-2 py-0.5 font-semibold text-blue-400">Primary</span>
                          {/if}
                          <span class="font-semibold text-slate-300" title={stream.codecLongName}>{stream.codecName}</span>
                        </p>
                        {#if streamSummary(stream)}
                          <p class="mt-1 text-xs text-slate-500">{streamSummary(stream)}</p>
                        {/if}
                      </li>
                    {/each}
                  </ul>
                </div>
              {/if}

              {#if technical.chapters.length > 0}
                <div>
                  <h3 class="text-xs font-bold uppercase tracking-[0.08em] text-slate-500">Chapters</h3>
                  <ul class="mt-2 space-y-1">
                    {#each technical.chapters as chapter}
                      <li class="flex gap-3 text-sm">
                        <span class="w-16 shrink-0 font-mono text-xs leading-6 text-slate-500">
                          {formatTicks(chapter.startTicks) ?? '0:00'}
                        </span>
                        <span class="min-w-0 truncate text-slate-300">{chapter.title}</span>
                      </li>
                    {/each}
                  </ul>
                </div>
              {/if}

              {#if !technical.format && technical.streams.length === 0 && technical.chapters.length === 0}
                <p class="text-sm text-slate-500">No technical metadata was archived for this video.</p>
              {/if}
            {/if}
          </div>
        {/if}
      </section>

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
    {#if userListId || platformListId}
      <div class="mb-6">
        {#key `${userListId ?? platformListId}`}
          <PlaylistPanel
            {mediaGuid}
            playlistId={userListId ?? platformListId ?? ''}
            kind={userListId ? 'user' : 'platform'}
            onEntriesChange={(guids) => (playlistGuids = guids)}
          />
        {/key}
      </div>
    {/if}

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
