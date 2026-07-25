<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { ArrowLeftOutline, CheckOutline, ExclamationCircleOutline, TrashBinOutline } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import PlaylistItemsManager from '$lib/components/profile/PlaylistItemsManager.svelte';
  import TargetNotePanel from '$lib/components/TargetNotePanel.svelte';
  import {
    deleteUserPlaylist,
    getUserPlaylist,
    updateUserPlaylist,
    type UserPlaylist
  } from '$lib/api/userPlaylists';

  let { params } = $props();


  let playlist = $state<UserPlaylist | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  let formName = $state('');
  let formDescription = $state('');
  let formBusy = $state(false);
  let formError = $state<string | null>(null);
  let formSaved = $state(false);

  let deleteModalOpen = $state(false);

  const formValid = $derived(formName.trim().length > 0 && formName.trim().length <= 255);
  const formDirty = $derived(
    playlist !== null &&
      (formName.trim() !== playlist.name || (formDescription.trim() || null) !== (playlist.description ?? null))
  );

  onMount(() => {
    void load();
  });

  async function load() {
    loading = true;
    loadError = null;
    try {
      const detail = await getUserPlaylist(params.playlistId);
      playlist = detail;
      formName = detail.name;
      formDescription = detail.description ?? '';
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the playlist.';
    } finally {
      loading = false;
    }
  }

  async function saveDetails(event: SubmitEvent) {
    event.preventDefault();
    if (!playlist || !formValid || formBusy) {
      return;
    }

    formBusy = true;
    formError = null;
    formSaved = false;
    try {
      const updated = await updateUserPlaylist(playlist.playlistId, {
        name: formName.trim(),
        description: formDescription.trim() || null
      });
      playlist = updated;
      formName = updated.name;
      formDescription = updated.description ?? '';
      formSaved = true;
    } catch (err) {
      formError = err instanceof Error ? err.message : 'Could not save the playlist.';
    } finally {
      formBusy = false;
    }
  }

  async function confirmDelete() {
    if (!playlist) {
      return;
    }
    await deleteUserPlaylist(playlist.playlistId);
    await goto('/profile/playlists');
  }

  function onItemsUpdated(updated: UserPlaylist) {
    playlist = updated;
  }
</script>

<svelte:head>
  <title>{playlist ? `${playlist.name} · FrostStream` : 'Playlist · FrostStream'}</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="playlist-edit-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="playlist-edit-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">
      {playlist?.name ?? 'Playlist'}
    </h1>
    <p class="mt-2 text-sm text-base-content/60">
      Rename the playlist, update its description, and reorder or remove its videos.
    </p>
  </div>

  {#if loading}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if loadError}
    <div class="rounded-2xl border border-error/30 bg-error/10 p-5 text-sm text-error" role="alert">
      <div class="flex items-start gap-3">
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{loadError}</span>
      </div>
      <a class="btn btn-sm btn-neutral mt-4 text-xs" href="/profile/playlists">
        Back to profile
      </a>
    </div>
  {:else if playlist}
    <div class="space-y-5">
      <section class="card border border-base-300 bg-base-100 p-5 sm:p-6" aria-label="Playlist details">
        <h2 class="text-base font-bold text-base-content">Details</h2>

        <form class="mt-4 space-y-4" onsubmit={saveDetails}>
          {#if formError}
            <div
              class="flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
              role="alert"
            >
              <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
              <span>{formError}</span>
            </div>
          {/if}

          <div>
            <label class="label mb-1.5 text-xs" for="playlist-name">Name</label>
            <input class="input w-full" id="playlist-name" bind:value={formName} maxlength={255} />
          </div>
          <div>
            <label class="label mb-1.5 text-xs" for="playlist-description">
              Description (optional)
            </label>
            <textarea class="textarea w-full" id="playlist-description" bind:value={formDescription} maxlength={2048} rows={3} placeholder="What belongs in this playlist?"></textarea>
          </div>

          <div class="flex flex-wrap items-center gap-3">
            <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={!formValid || !formDirty || formBusy}>
              {#if formBusy}
                <span class="loading loading-spinner loading-xs mr-1.5"></span>
              {/if}
              Save changes
            </button>
            {#if formSaved && !formDirty}
              <span class="flex items-center gap-1.5 text-xs font-semibold text-success">
                <CheckOutline class="h-3.5 w-3.5" />
                Saved
              </span>
            {/if}
          </div>
        </form>
      </section>

      <TargetNotePanel
        targetType="playlist"
        targetId={playlist.playlistId}
        targetLabel="Playlist"
        initialNote={playlist.userNote}
        onChange={(note) => {
          if (playlist) {
            playlist = { ...playlist, userNote: note };
          }
        }}
      />

      <section class="card border border-base-300 bg-base-100 p-5 sm:p-6" aria-label="Playlist items">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h2 class="text-base font-bold text-base-content">
            Videos
            <span class="ml-1.5 text-sm font-medium text-base-content/50">
              {playlist.itemCount} {playlist.itemCount === 1 ? 'item' : 'items'}
            </span>
          </h2>
        </div>

        <div class="mt-4">
          <PlaylistItemsManager {playlist} onUpdated={onItemsUpdated} />
        </div>
      </section>

      <section class="rounded-2xl border border-error/30 bg-base-100 p-5 shadow-xl shadow-black/15 sm:p-6" aria-label="Danger zone">
        <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h2 class="text-base font-bold text-base-content">Delete this playlist</h2>
            <p class="mt-1 text-sm text-base-content/60">The videos in it stay on the server.</p>
          </div>
          <button class="btn btn-sm btn-ghost shrink-0 text-xs" onclick={() => (deleteModalOpen = true)}>
            <TrashBinOutline class="mr-1.5 h-3.5 w-3.5" />
            Delete playlist
          </button>
        </div>
      </section>

      <div class="border-t border-base-300/70 pt-5">
        <a class="btn btn-sm btn-ghost text-xs" href="/profile/playlists">
          <ArrowLeftOutline class="mr-1.5 h-4 w-4" />
          Back
        </a>
      </div>
    </div>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete playlist"
  message={`Delete playlist "${playlist?.name ?? ''}"? The videos in it stay on the server.`}
  confirmLabel="Delete playlist"
  onConfirm={confirmDelete}
/>
