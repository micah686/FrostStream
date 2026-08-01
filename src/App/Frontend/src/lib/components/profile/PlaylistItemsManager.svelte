<script lang="ts">
  import {
    ChevronDown,
    ChevronUp,
    CircleAlert,
    ListMusic,
    Trash2
  } from '@lucide/svelte';
  import { accentFor, formatDuration, formatRelativeDate, initialsFor } from '$lib/media';
  import {
    removeUserPlaylistItem,
    reorderUserPlaylistItems,
    type UserPlaylist
  } from '$lib/api/userPlaylists';

  interface MediaSummary {
    title: string;
    thumbnailStoragePath?: string | null;
    durationSeconds?: number | null;
    account: { accountId: number; accountName: string };
  }

  interface Props {
    playlist: UserPlaylist;
    onUpdated: (playlist: UserPlaylist) => void;
  }

  let { playlist, onUpdated }: Props = $props();

  let mediaCards = $state<Record<string, MediaSummary | null>>({});
  let busyItemGuid = $state<string | null>(null);
  let itemActionError = $state<string | null>(null);

  $effect(() => {
    void hydrateMediaCards(playlist);
  });

  async function hydrateMediaCards(current: UserPlaylist) {
    const missing = (current.items ?? [])
      .map((item) => item.mediaGuid)
      .filter((guid) => !(guid in mediaCards));

    await Promise.all(
      missing.map(async (guid) => {
        try {
          const response = await fetch(`/api/metadata/${guid}`);
          mediaCards[guid] = response.ok ? ((await response.json()) as MediaSummary) : null;
        } catch {
          mediaCards[guid] = null;
        }
      })
    );
  }

  async function removeItem(mediaGuid: string) {
    busyItemGuid = mediaGuid;
    itemActionError = null;
    try {
      onUpdated(await removeUserPlaylistItem(playlist.playlistId, mediaGuid));
    } catch (err) {
      itemActionError = err instanceof Error ? err.message : 'Could not remove the item.';
    } finally {
      busyItemGuid = null;
    }
  }

  async function moveItem(mediaGuid: string, direction: -1 | 1) {
    const items = playlist.items;
    if (!items) {
      return;
    }

    const index = items.findIndex((item) => item.mediaGuid === mediaGuid);
    const target = index + direction;
    if (index < 0 || target < 0 || target >= items.length) {
      return;
    }

    const order = items.map((item) => item.mediaGuid);
    [order[index], order[target]] = [order[target], order[index]];

    busyItemGuid = mediaGuid;
    itemActionError = null;
    try {
      onUpdated(await reorderUserPlaylistItems(playlist.playlistId, order));
    } catch (err) {
      itemActionError = err instanceof Error ? err.message : 'Could not reorder the playlist.';
    } finally {
      busyItemGuid = null;
    }
  }

  function thumbnailUrl(mediaGuid: string): string | null {
    return mediaCards[mediaGuid]?.thumbnailStoragePath ? `/api/media/watch/${mediaGuid}/thumbnail` : null;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

{#if !playlist.items || playlist.items.length === 0}
  <div class="rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
    <ListMusic class="mx-auto h-9 w-9 text-base-content/30" />
    <p class="mt-4 text-sm font-semibold text-base-content/80">This playlist is empty</p>
    <p class="mt-1 text-sm text-base-content/50">Add videos to it from the watch page.</p>
  </div>
{:else}
  {#if itemActionError}
    <div
      class="alert alert-error mb-3 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{itemActionError}</span>
    </div>
  {/if}

  <ol class="space-y-2">
    {#each playlist.items as item, index (item.mediaGuid)}
      {@const media = mediaCards[item.mediaGuid]}
      <li
        class="flex items-center gap-3 rounded-lg border border-base-content/20 bg-base-100 px-3 py-2.5 transition hover:border-base-content/30 hover:bg-base-300/30 sm:px-4"
      >
        <span class="w-6 shrink-0 text-center font-mono text-xs text-base-content/40">{index + 1}</span>

        <a
          href={`/watch/${item.mediaGuid}`}
          class={`relative block aspect-video w-24 shrink-0 overflow-hidden rounded-lg bg-gradient-to-br ${accentFor(item.mediaGuid)} shadow shadow-black/20`}
          aria-label={media ? `Watch ${media.title}` : 'Watch'}
        >
          {#if media}
            <span class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-sm font-black text-white/15">
              {initialsFor(media.account.accountName)}
            </span>
          {/if}
          {#if thumbnailUrl(item.mediaGuid)}
            <img
              src={thumbnailUrl(item.mediaGuid)}
              alt=""
              loading="lazy"
              decoding="async"
              class="absolute inset-0 h-full w-full object-cover"
              onerror={hideBrokenImage}
            />
          {/if}
          {#if media && formatDuration(media.durationSeconds)}
            <span class="absolute bottom-1 right-1 rounded bg-black/75 px-1 py-0.5 text-[10px] font-semibold text-white">
              {formatDuration(media.durationSeconds)}
            </span>
          {/if}
        </a>

        <div class="min-w-0 flex-1">
          {#if media}
            <a href={`/watch/${item.mediaGuid}`} class="block truncate text-sm font-semibold text-base-content/90 hover:text-base-content">
              {media.title}
            </a>
            <p class="mt-0.5 truncate text-xs text-base-content/50">
              {[media.account.accountName, formatRelativeDate(item.addedAt) ? `added ${formatRelativeDate(item.addedAt)}` : null]
                .filter(Boolean)
                .join(' · ')}
            </p>
          {:else if media === null}
            <p class="truncate text-sm font-semibold text-base-content/50">Media unavailable</p>
            <p class="mt-0.5 truncate font-mono text-xs text-base-content/40">{item.mediaGuid}</p>
          {:else}
            <div class="h-4 w-40 animate-pulse rounded bg-base-300/70"></div>
          {/if}
        </div>

        <div class="flex shrink-0 items-center gap-1.5">
          {#if busyItemGuid === item.mediaGuid}
            <span class="loading loading-spinner loading-xs mx-2"></span>
          {:else}
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Move up"
              aria-label="Move up"
              disabled={index === 0 || busyItemGuid !== null}
              onclick={() => moveItem(item.mediaGuid, -1)}
            >
              <ChevronUp class="h-4 w-4" />
            </button>
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Move down"
              aria-label="Move down"
              disabled={index === (playlist.items?.length ?? 0) - 1 || busyItemGuid !== null}
              onclick={() => moveItem(item.mediaGuid, 1)}
            >
              <ChevronDown class="h-4 w-4" />
            </button>
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Remove from playlist"
              aria-label="Remove from playlist"
              disabled={busyItemGuid !== null}
              onclick={() => removeItem(item.mediaGuid)}
            >
              <Trash2 class="h-4 w-4" />
            </button>
          {/if}
        </div>
      </li>
    {/each}
  </ol>
{/if}
