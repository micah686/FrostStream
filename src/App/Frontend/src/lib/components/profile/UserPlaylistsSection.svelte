<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Label, Spinner, Textarea } from 'flowbite-svelte';
  import {
    ArrowLeftOutline,
    ExclamationCircleOutline,
    ListMusicOutline,
    PenOutline,
    PlusOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import PlaylistItemsManager from '$lib/components/profile/PlaylistItemsManager.svelte';
  import { formatRelativeDate } from '$lib/media';
  import {
    createUserPlaylist,
    deleteUserPlaylist,
    getUserPlaylist,
    listUserPlaylists,
    type UserPlaylist
  } from '$lib/api/userPlaylists';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';

  let playlists = $state<UserPlaylist[]>([]);
  let loading = $state(true);
  let listError = $state<string | null>(null);

  // Create form
  let formOpen = $state(false);
  let formName = $state('');
  let formDescription = $state('');
  let formBusy = $state(false);
  let formError = $state<string | null>(null);

  const formValid = $derived(formName.trim().length > 0 && formName.trim().length <= 255);

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

  function openCreateForm() {
    formName = '';
    formDescription = '';
    formError = null;
    formOpen = true;
  }

  async function saveForm(event: SubmitEvent) {
    event.preventDefault();
    if (!formValid) {
      return;
    }

    formBusy = true;
    formError = null;
    try {
      const created = await createUserPlaylist({
        name: formName.trim(),
        description: formDescription.trim() || null
      });
      playlists = [created, ...playlists];
      formOpen = false;
    } catch (err) {
      formError = err instanceof Error ? err.message : 'Could not save the playlist.';
    } finally {
      formBusy = false;
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
  class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6"
  aria-labelledby="user-playlists-title"
>
  {#if !selected}
    <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
      <div>
        <h2 id="user-playlists-title" class="text-base font-bold text-slate-100">Playlists</h2>
        <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
          Your private playlists on this server. They reference archived media and are visible only to you.
        </p>
      </div>
      <Button color="dark" class={outlineButtonClass} onclick={openCreateForm}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        New playlist
      </Button>
    </div>

    {#if listError}
      <div
        class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{listError}</span>
      </div>
    {/if}

    {#if formOpen}
      <form
        class="mt-5 space-y-4 rounded-xl border border-slate-800/80 bg-slate-950/30 p-4"
        onsubmit={saveForm}
      >
        <h3 class="text-sm font-semibold text-slate-200">New playlist</h3>
        {#if formError}
          <p class="text-sm text-red-300" role="alert">{formError}</p>
        {/if}
        <div>
          <Label for="playlist-name" class="mb-1.5 text-xs! font-semibold! text-slate-400!">Name</Label>
          <Input id="playlist-name" bind:value={formName} maxlength={255} placeholder="Watch later, favourites…" class={inputClass} />
        </div>
        <div>
          <Label for="playlist-description" class="mb-1.5 text-xs! font-semibold! text-slate-400!">
            Description (optional)
          </Label>
          <Textarea
            id="playlist-description"
            bind:value={formDescription}
            maxlength={2048}
            rows={2}
            placeholder="What belongs in this playlist?"
            class={inputClass}
          />
        </div>
        <div class="flex flex-wrap gap-2">
          <Button
            type="submit"
            color="blue"
            disabled={!formValid || formBusy}
            class="border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
          >
            {#if formBusy}
              <Spinner size="4" class="mr-1.5" />
            {/if}
            Create playlist
          </Button>
          <Button color="dark" class={outlineButtonClass} disabled={formBusy} onclick={() => (formOpen = false)}>
            Cancel
          </Button>
        </div>
      </form>
    {/if}

    {#if loading}
      <div class="mt-10 flex justify-center">
        <Spinner size="8" />
      </div>
    {:else if playlists.length === 0}
      <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
        <ListMusicOutline class="mx-auto h-9 w-9 text-slate-700" />
        <p class="mt-4 text-sm font-semibold text-slate-300">No playlists yet</p>
        <p class="mt-1 text-sm text-slate-500">Create one to group archived videos however you like.</p>
      </div>
    {:else}
      <div class="mt-5 space-y-2">
        {#each playlists as playlist (playlist.playlistId)}
          <article
            class="flex min-h-[3.95rem] flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:flex-row sm:items-center sm:px-4"
          >
            <button
              type="button"
              class="flex min-w-0 flex-1 items-center gap-3 text-left"
              onclick={() => openDetail(playlist)}
              aria-label={`Open playlist ${playlist.name}`}
            >
              <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                <ListMusicOutline class="h-4.5 w-4.5" />
              </span>
              <span class="min-w-0">
                <span class="block truncate text-sm font-semibold text-slate-100">{playlist.name}</span>
                <span class="mt-0.5 block truncate text-xs text-slate-400">
                  {playlist.description || playlistMeta(playlist)}
                </span>
              </span>
            </button>

            <div class="flex shrink-0 items-center gap-2 sm:ml-auto">
              <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                {playlistMeta(playlist)}
              </span>
              <a
                href={editHref(playlist)}
                class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
                title="Edit playlist"
                aria-label={`Edit playlist ${playlist.name}`}
              >
                <PenOutline class="h-4 w-4" />
              </a>
              <button
                type="button"
                class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200"
                title="Delete playlist"
                aria-label={`Delete playlist ${playlist.name}`}
                onclick={() => requestDelete(playlist)}
              >
                <TrashBinOutline class="h-4 w-4" />
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
          class="flex items-center gap-1.5 text-xs font-semibold text-slate-500 transition hover:text-slate-300"
          onclick={closeDetail}
        >
          <ArrowLeftOutline class="h-3.5 w-3.5" />
          All playlists
        </button>
        <h2 id="user-playlists-title" class="mt-2 text-base font-bold text-slate-100">{selected.name}</h2>
        {#if selected.description}
          <p class="mt-1 max-w-3xl text-sm leading-6 text-slate-400">{selected.description}</p>
        {/if}
        <p class="mt-1 text-xs text-slate-500">{playlistMeta(selected)}</p>
      </div>

      <div class="flex shrink-0 gap-2">
        <Button href={editHref(selected)} color="dark" class={outlineButtonClass}>
          <PenOutline class="mr-1.5 h-3.5 w-3.5" />
          Edit
        </Button>
        <Button
          color="dark"
          class="border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-red-500/60! hover:bg-red-500/10! hover:text-red-200!"
          onclick={() => requestDelete(selected!)}
        >
          <TrashBinOutline class="mr-1.5 h-3.5 w-3.5" />
          Delete
        </Button>
      </div>
    </div>

    {#if detailError}
      <div
        class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{detailError}</span>
      </div>
    {:else if detailLoading}
      <div class="mt-10 flex justify-center">
        <Spinner size="8" />
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
