<script lang="ts">
  import { onMount } from 'svelte';
  import {
    ArrowLeft,
    CircleAlert,
    ListMusic,
    Pen,
    Plus,
    Trash2
  } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import PlaylistItemsManager from '$lib/components/profile/PlaylistItemsManager.svelte';
  import { formatRelativeDate } from '$lib/media';
  import {
    deleteUserPlaylist,
    getUserPlaylist,
    listUserPlaylists,
    type UserPlaylist
  } from '$lib/api/userPlaylists';


  let playlists = $state<UserPlaylist[]>([]);
  let loading = $state(true);
  let listError = $state<string | null>(null);

  // Delete
  let deleteTarget = $state<UserPlaylist | null>(null);
  let deleteModalOpen = $state(false);

  // Detail view
  let selected = $state<UserPlaylist | null>(null);
  let detailLoading = $state(false);
  let detailError = $state<string | null>(null);

  onMount(() => {
    void load();
  });

  async function load() {
    loading = true;
    listError = null;
    try {
      playlists = await listUserPlaylists();
    } catch (err) {
      listError = err instanceof Error ? err.message : 'Could not load your playlists.';
    } finally {
      loading = false;
    }
  }

  function replaceInList(playlist: UserPlaylist) {
    playlists = playlists.map((item) => (item.playlistId === playlist.playlistId ? playlist : item));
  }

  function requestDelete(playlist: UserPlaylist) {
    deleteTarget = playlist;
    deleteModalOpen = true;
  }

  async function confirmDelete() {
    if (!deleteTarget) {
      return;
    }
    const id = deleteTarget.playlistId;
    await deleteUserPlaylist(id);
    playlists = playlists.filter((item) => item.playlistId !== id);
    if (selected?.playlistId === id) {
      selected = null;
    }
    deleteTarget = null;
  }

  async function openDetail(playlist: UserPlaylist) {
    selected = playlist;
    detailError = null;
    detailLoading = true;
    try {
      const detail = await getUserPlaylist(playlist.playlistId);
      selected = detail;
      replaceInList(detail);
    } catch (err) {
      detailError = err instanceof Error ? err.message : 'Could not load the playlist.';
    } finally {
      detailLoading = false;
    }
  }

  function closeDetail() {
    selected = null;
    detailError = null;
  }

  function applyUpdatedDetail(updated: UserPlaylist) {
    selected = updated;
    replaceInList(updated);
  }

  function playlistMeta(playlist: UserPlaylist): string {
    return [
      `${playlist.itemCount} ${playlist.itemCount === 1 ? 'item' : 'items'}`,
      formatRelativeDate(playlist.updatedAt) ? `updated ${formatRelativeDate(playlist.updatedAt)}` : null
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function editHref(playlist: UserPlaylist): string {
    return `/profile/playlists/${encodeURIComponent(playlist.playlistId)}`;
  }
</script>

<section
  class="card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6"
  aria-labelledby="user-playlists-title"
>
  {#if !selected}
    <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
      <div>
        <h2 id="user-playlists-title" class="text-base font-bold text-base-content">Playlists</h2>
        <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
          Your private playlists on this server. They reference archived media and are visible only to you.
        </p>
      </div>
      <a class="btn btn-sm btn-neutral text-xs" href="/profile/playlists/new">
        <Plus class="mr-1.5 h-3.5 w-3.5" />
        New playlist
      </a>
    </div>

    {#if listError}
      <div
        class="alert alert-error mt-5 text-sm"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{listError}</span>
      </div>
    {/if}

    {#if loading}
      <div class="mt-10 flex justify-center">
        <span class="loading loading-spinner loading-md"></span>
      </div>
    {:else if playlists.length === 0}
      <div class="mt-5 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/30 p-8 text-center">
        <ListMusic class="mx-auto h-9 w-9 text-base-content/30" />
        <p class="mt-4 text-sm font-semibold text-base-content/80">No playlists yet</p>
        <p class="mt-1 text-sm text-base-content/50">Create one to group archived videos however you like.</p>
      </div>
    {:else}
      <div class="mt-5 space-y-2">
        {#each playlists as playlist (playlist.playlistId)}
          <article
            class="flex min-h-[3.95rem] flex-col gap-3 rounded-field border-[length:var(--border)] border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:flex-row sm:items-center sm:px-4"
          >
            <button
              type="button"
              class="flex min-w-0 flex-1 items-center gap-3 text-left"
              onclick={() => openDetail(playlist)}
              aria-label={`Open playlist ${playlist.name}`}
            >
              <span class="grid h-9 w-9 shrink-0 place-items-center rounded-field bg-base-300/70 text-primary">
                <ListMusic class="h-4.5 w-4.5" />
              </span>
              <span class="min-w-0">
                <span class="block truncate text-sm font-semibold text-base-content">{playlist.name}</span>
                <span class="mt-0.5 block truncate text-xs text-base-content/60">
                  {playlist.description || playlistMeta(playlist)}
                </span>
              </span>
            </button>

            <div class="flex shrink-0 items-center gap-2 sm:ml-auto">
              <a
                href={editHref(playlist)}
                class="btn btn-sm btn-neutral text-xs"
                title="Edit playlist"
                aria-label={`Edit playlist ${playlist.name}`}
              >
                <Pen class="mr-1.5 h-4 w-4" />
                Edit
              </a>
              <button
                type="button"
                class="btn btn-sm btn-neutral text-xs"
                title="Delete playlist"
                aria-label={`Delete playlist ${playlist.name}`}
                onclick={() => requestDelete(playlist)}
              >
                <Trash2 class="mr-1.5 h-4 w-4" />
                Delete
              </button>
            </div>
          </article>
        {/each}
      </div>
    {/if}
  {:else}
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <button
          type="button"
          class="flex items-center gap-1.5 text-xs font-semibold text-base-content/50 transition hover:text-base-content/80"
          onclick={closeDetail}
        >
          <ArrowLeft class="h-3.5 w-3.5" />
          All playlists
        </button>
        <h2 id="user-playlists-title" class="mt-2 text-base font-bold text-base-content">{selected.name}</h2>
        {#if selected.description}
          <p class="mt-1 max-w-3xl text-sm leading-6 text-base-content/60">{selected.description}</p>
        {/if}
        <p class="mt-1 text-xs text-base-content/50">{playlistMeta(selected)}</p>
      </div>

      <div class="flex shrink-0 gap-2">
        <a class="btn btn-sm btn-neutral text-xs" href={editHref(selected)}>
          <Pen class="mr-1.5 h-3.5 w-3.5" />
          Edit
        </a>
        <button class="btn btn-sm btn-neutral text-xs" onclick={() => requestDelete(selected!)}>
          <Trash2 class="mr-1.5 h-3.5 w-3.5" />
          Delete
        </button>
      </div>
    </div>

    {#if detailError}
      <div
        class="alert alert-error mt-5 text-sm"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{detailError}</span>
      </div>
    {:else if detailLoading}
      <div class="mt-10 flex justify-center">
        <span class="loading loading-spinner loading-md"></span>
      </div>
    {:else}
      <div class="mt-5">
        <PlaylistItemsManager playlist={selected} onUpdated={applyUpdatedDetail} />
      </div>
    {/if}
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete playlist"
  message={`Delete playlist "${deleteTarget?.name ?? ''}"? The videos in it stay on the server.`}
  confirmLabel="Delete playlist"
  onConfirm={confirmDelete}
/>
