<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { Button, Input, Label, Spinner, Textarea } from 'flowbite-svelte';
  import { ArrowLeftOutline, CheckOutline, ExclamationCircleOutline, TrashBinOutline } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import PlaylistItemsManager from '$lib/components/profile/PlaylistItemsManager.svelte';
  import {
    deleteUserPlaylist,
    getUserPlaylist,
    updateUserPlaylist,
    type UserPlaylist
  } from '$lib/api/userPlaylists';

  let { params } = $props();

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';

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
    await goto('/profile?section=Playlists');
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
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-blue-400">Profile</p>
    <h1 id="playlist-edit-title" class="mt-2 text-2xl font-bold tracking-tight text-slate-100">
      {playlist?.name ?? 'Playlist'}
    </h1>
    <p class="mt-2 text-sm text-slate-400">
      Rename the playlist, update its description, and reorder or remove its videos.
    </p>
  </div>

  {#if loading}
    <div class="mt-16 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if loadError}
    <div class="rounded-2xl border border-red-900/60 bg-red-950/35 p-5 text-sm text-red-300" role="alert">
      <div class="flex items-start gap-3">
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{loadError}</span>
      </div>
      <Button
        href="/profile?section=Playlists"
        color="dark"
        class="mt-4 border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
      >
        Back to profile
      </Button>
    </div>
  {:else if playlist}
    <div class="space-y-5">
      <section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6" aria-label="Playlist details">
        <h2 class="text-base font-bold text-slate-100">Details</h2>

        <form class="mt-4 space-y-4" onsubmit={saveDetails}>
          {#if formError}
            <div
              class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
              role="alert"
            >
              <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
              <span>{formError}</span>
            </div>
          {/if}

          <div>
            <Label for="playlist-name" class="mb-1.5 text-xs! font-semibold! text-slate-400!">Name</Label>
            <Input id="playlist-name" bind:value={formName} maxlength={255} class={inputClass} />
          </div>
          <div>
            <Label for="playlist-description" class="mb-1.5 text-xs! font-semibold! text-slate-400!">
              Description (optional)
            </Label>
            <Textarea
              id="playlist-description"
              bind:value={formDescription}
              maxlength={2048}
              rows={3}
              placeholder="What belongs in this playlist?"
              class={inputClass}
            />
          </div>

          <div class="flex flex-wrap items-center gap-3">
            <Button
              type="submit"
              color="blue"
              disabled={!formValid || !formDirty || formBusy}
              class="border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
            >
              {#if formBusy}
                <Spinner size="4" class="mr-1.5" />
              {/if}
              Save changes
            </Button>
            {#if formSaved && !formDirty}
              <span class="flex items-center gap-1.5 text-xs font-semibold text-emerald-400">
                <CheckOutline class="h-3.5 w-3.5" />
                Saved
              </span>
            {/if}
          </div>
        </form>
      </section>

      <section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6" aria-label="Playlist items">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h2 class="text-base font-bold text-slate-100">
            Videos
            <span class="ml-1.5 text-sm font-medium text-slate-500">
              {playlist.itemCount} {playlist.itemCount === 1 ? 'item' : 'items'}
            </span>
          </h2>
        </div>

        <div class="mt-4">
          <PlaylistItemsManager {playlist} onUpdated={onItemsUpdated} />
        </div>
      </section>

      <section class="rounded-2xl border border-red-900/40 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6" aria-label="Danger zone">
        <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h2 class="text-base font-bold text-slate-100">Delete this playlist</h2>
            <p class="mt-1 text-sm text-slate-400">The videos in it stay on the server.</p>
          </div>
          <Button
            color="dark"
            class="shrink-0 border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-red-500/60! hover:bg-red-500/10! hover:text-red-200!"
            onclick={() => (deleteModalOpen = true)}
          >
            <TrashBinOutline class="mr-1.5 h-3.5 w-3.5" />
            Delete playlist
          </Button>
        </div>
      </section>

      <div class="border-t border-slate-800/70 pt-5">
        <Button
          href="/profile?section=Playlists"
          color="dark"
          class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
        >
          <ArrowLeftOutline class="mr-1.5 h-4 w-4" />
          Back
        </Button>
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
