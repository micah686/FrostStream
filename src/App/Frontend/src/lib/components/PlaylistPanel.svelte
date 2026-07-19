<script lang="ts">
  import { Spinner } from 'flowbite-svelte';
  import { ExclamationCircleOutline, ListMusicOutline, PlaySolid } from 'flowbite-svelte-icons';
  import { accentFor, formatDuration } from '$lib/media';
  import { getUserPlaylist } from '$lib/api/userPlaylists';
  import { getPlatformPlaylist } from '$lib/api/playlists';

  interface PanelEntry {
    /** Null when the entry is not playable (platform item not downloaded yet). */
    mediaGuid: string | null;
    title: string;
    subtitle: string | null;
    durationSeconds: number | null;
  }

  interface MediaSummary {
    title: string;
    durationSeconds?: number | null;
    account: { accountName: string };
  }

  interface Props {
    mediaGuid: string;
    playlistId: string;
    kind: 'user' | 'platform';
    /** Reports the ordered media guids after each load (null = entry not playable yet). */
    onEntriesChange?: (mediaGuids: (string | null)[]) => void;
  }

  let { mediaGuid, playlistId, kind, onEntriesChange }: Props = $props();

  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let title = $state('Playlist');
  let subtitle = $state<string | null>(null);
  let entries = $state<PanelEntry[]>([]);
  let listElement = $state<HTMLOListElement | null>(null);

  const queryParam = $derived(kind === 'user' ? 'ulist' : 'list');
  const currentIndex = $derived(entries.findIndex((entry) => entry.mediaGuid === mediaGuid));
  const playableCount = $derived(entries.filter((entry) => entry.mediaGuid !== null).length);

  // Reload only when the playlist itself changes; navigating between videos in the
  // same playlist just moves the highlight.
  $effect(() => {
    void playlistId;
    void kind;
    void load();
  });

  // Keep the active row visible when the panel loads or the video changes.
  $effect(() => {
    if (loading || currentIndex < 0 || !listElement) {
      return;
    }
    const row = listElement.querySelector('[data-current="true"]');
    row?.scrollIntoView({ block: 'nearest' });
  });

  async function load() {
    loading = true;
    loadError = null;
    entries = [];
    try {
      if (kind === 'user') {
        await loadUserPlaylist();
      } else {
        await loadPlatformPlaylist();
      }
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the playlist.';
    } finally {
      loading = false;
      onEntriesChange?.(entries.map((entry) => entry.mediaGuid));
    }
  }

  async function loadUserPlaylist() {
    const playlist = await getUserPlaylist(playlistId);
    title = playlist.name;
    subtitle = 'Your playlist';

    const items = playlist.items ?? [];
    const summaries = await Promise.all(
      items.map(async (item): Promise<MediaSummary | null> => {
        try {
          const response = await fetch(`/api/metadata/${item.mediaGuid}`);
          return response.ok ? ((await response.json()) as MediaSummary) : null;
        } catch {
          return null;
        }
      })
    );

    entries = items.map((item, index) => ({
      mediaGuid: item.mediaGuid,
      title: summaries[index]?.title ?? 'Media unavailable',
      subtitle: summaries[index]?.account.accountName ?? null,
      durationSeconds: summaries[index]?.durationSeconds ?? null
    }));
  }

  async function loadPlatformPlaylist() {
    const playlist = await getPlatformPlaylist(playlistId);
    title = playlist.title || playlist.sourceUrl;
    subtitle = 'Downloaded playlist';

    entries = (playlist.items ?? [])
      .slice()
      .sort((a, b) => a.playlistIndex - b.playlistIndex)
      .map((item) => ({
        mediaGuid: item.mediaGuid,
        title: item.entryTitle || item.entryUrl,
        subtitle: item.mediaGuid ? null : statusLabel(item.jobStatus),
        durationSeconds: null
      }));
  }

  function statusLabel(jobStatus: string): string {
    const normalized = jobStatus.replace(/_/g, ' ').toLowerCase();
    return `Not available — ${normalized}`;
  }

  function thumbnailUrl(guid: string): string {
    return `/api/media/watch/${guid}/thumbnail`;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<section
  class="overflow-hidden rounded-2xl border border-slate-800 bg-[#151a26] shadow-xl shadow-black/20"
  aria-label="Playlist"
>
  <header class="border-b border-slate-800 px-4 py-3">
    <p class="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-500">
      <ListMusicOutline class="h-3.5 w-3.5" />
      {subtitle ?? 'Playlist'}
    </p>
    <h2 class="mt-1 truncate text-sm font-bold text-white" {title}>{title}</h2>
    {#if !loading && !loadError}
      <p class="mt-0.5 text-xs text-slate-500">
        {currentIndex >= 0 ? `${currentIndex + 1} / ${entries.length}` : `${entries.length} ${entries.length === 1 ? 'video' : 'videos'}`}
        {#if playableCount < entries.length}
          · {playableCount} available
        {/if}
      </p>
    {/if}
  </header>

  {#if loading}
    <div class="flex justify-center py-8">
      <Spinner size="5" />
    </div>
  {:else if loadError}
    <div class="flex items-start gap-2 p-4 text-sm text-red-300" role="alert">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {:else if entries.length === 0}
    <p class="p-4 text-sm text-slate-500">This playlist is empty.</p>
  {:else}
    <ol bind:this={listElement} class="max-h-[26rem] overflow-y-auto py-1.5">
      {#each entries as entry, index (index)}
        {@const isCurrent = entry.mediaGuid !== null && entry.mediaGuid === mediaGuid}
        <li data-current={isCurrent}>
          {#if entry.mediaGuid}
            <a
              href={`/watch/${entry.mediaGuid}?${queryParam}=${encodeURIComponent(playlistId)}`}
              aria-current={isCurrent ? 'page' : undefined}
              class={[
                'flex items-center gap-2.5 px-3 py-2 transition',
                isCurrent ? 'bg-blue-500/10' : 'hover:bg-slate-800/60'
              ]}
            >
              <span class="grid w-5 shrink-0 place-items-center font-mono text-[11px] text-slate-600">
                {#if isCurrent}
                  <PlaySolid class="h-3 w-3 text-blue-400" />
                {:else}
                  {index + 1}
                {/if}
              </span>
              <span
                class={`relative block aspect-video w-20 shrink-0 overflow-hidden rounded-md bg-gradient-to-br ${accentFor(entry.mediaGuid)}`}
              >
                <img
                  src={thumbnailUrl(entry.mediaGuid)}
                  alt=""
                  loading="lazy"
                  decoding="async"
                  class="absolute inset-0 h-full w-full object-cover"
                  onerror={hideBrokenImage}
                />
                {#if formatDuration(entry.durationSeconds)}
                  <span class="absolute bottom-1 right-1 rounded bg-black/75 px-1 py-0.5 text-[9px] font-semibold text-white">
                    {formatDuration(entry.durationSeconds)}
                  </span>
                {/if}
              </span>
              <span class="min-w-0">
                <span
                  class={[
                    'line-clamp-2 text-xs font-semibold leading-snug',
                    isCurrent ? 'text-blue-300' : 'text-slate-200'
                  ]}
                >
                  {entry.title}
                </span>
                {#if entry.subtitle}
                  <span class="mt-0.5 block truncate text-[11px] text-slate-500">{entry.subtitle}</span>
                {/if}
              </span>
            </a>
          {:else}
            <div class="flex items-center gap-2.5 px-3 py-2 opacity-50">
              <span class="grid w-5 shrink-0 place-items-center font-mono text-[11px] text-slate-600">
                {index + 1}
              </span>
              <span class="relative block aspect-video w-20 shrink-0 overflow-hidden rounded-md bg-slate-800/70"></span>
              <span class="min-w-0">
                <span class="line-clamp-2 text-xs font-semibold leading-snug text-slate-400">{entry.title}</span>
                {#if entry.subtitle}
                  <span class="mt-0.5 block truncate text-[11px] text-slate-600">{entry.subtitle}</span>
                {/if}
              </span>
            </div>
          {/if}
        </li>
      {/each}
    </ol>
  {/if}
</section>
