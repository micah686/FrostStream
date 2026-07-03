<script lang="ts">
  import { Spinner } from 'flowbite-svelte';
  import {
    CheckOutline,
    ExclamationCircleOutline,
    FolderOutline,
    FolderSolid,
    ListMusicOutline,
    PlusOutline
  } from 'flowbite-svelte-icons';
  import {
    addUserPlaylistItem,
    createUserPlaylist,
    getUserPlaylist,
    listUserPlaylists,
    removeUserPlaylistItem,
    type UserPlaylist
  } from '$lib/api/userPlaylists';

  interface Props {
    mediaGuid: string;
  }

  let { mediaGuid }: Props = $props();

  let open = $state(false);
  let loaded = $state(false);
  let loading = $state(false);
  let loadError = $state<string | null>(null);
  let playlists = $state<UserPlaylist[]>([]);
  let membership = $state<Record<string, boolean>>({});
  let busyPlaylistId = $state<string | null>(null);
  let toggleError = $state<string | null>(null);

  let createOpen = $state(false);
  let createName = $state('');
  let createBusy = $state(false);

  let container = $state<HTMLDivElement | null>(null);

  const savedCount = $derived(playlists.filter((p) => membership[p.playlistId]).length);
  const isSaved = $derived(loaded && savedCount > 0);

  // Membership belongs to one video; navigating to another (e.g. via Up next) must reset it.
  $effect(() => {
    void mediaGuid;
    open = false;
    loaded = false;
    playlists = [];
    membership = {};
    loadError = null;
    toggleError = null;
    createOpen = false;
    createName = '';
  });

  $effect(() => {
    if (!open) {
      return;
    }
    const onPointerDown = (event: PointerEvent) => {
      if (container && event.target instanceof Node && !container.contains(event.target)) {
        open = false;
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        open = false;
      }
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  });

  function toggleOpen() {
    open = !open;
    if (open && !loaded && !loading) {
      void load();
    }
  }

  async function load() {
    loading = true;
    loadError = null;
    try {
      const list = await listUserPlaylists();
      // The list endpoint omits items, so membership needs one detail fetch per playlist.
      const details = await Promise.all(
        list.map(async (playlist) => {
          try {
            return await getUserPlaylist(playlist.playlistId);
          } catch {
            return playlist;
          }
        })
      );
      const map: Record<string, boolean> = {};
      for (const detail of details) {
        map[detail.playlistId] = (detail.items ?? []).some((item) => item.mediaGuid === mediaGuid);
      }
      playlists = list;
      membership = map;
      loaded = true;
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load your playlists.';
    } finally {
      loading = false;
    }
  }

  async function toggleMembership(playlist: UserPlaylist) {
    if (busyPlaylistId) {
      return;
    }
    busyPlaylistId = playlist.playlistId;
    toggleError = null;
    const wasIn = membership[playlist.playlistId] === true;
    try {
      const updated = wasIn
        ? await removeUserPlaylistItem(playlist.playlistId, mediaGuid)
        : await addUserPlaylistItem(playlist.playlistId, mediaGuid);
      membership[playlist.playlistId] = !wasIn;
      playlists = playlists.map((item) => (item.playlistId === updated.playlistId ? updated : item));
    } catch (err) {
      toggleError = err instanceof Error ? err.message : 'Could not update the playlist.';
    } finally {
      busyPlaylistId = null;
    }
  }

  async function createAndAdd(event: SubmitEvent) {
    event.preventDefault();
    const name = createName.trim();
    if (!name || createBusy) {
      return;
    }
    createBusy = true;
    toggleError = null;
    try {
      const created = await createUserPlaylist({ name });
      const withItem = await addUserPlaylistItem(created.playlistId, mediaGuid);
      playlists = [withItem, ...playlists];
      membership[withItem.playlistId] = true;
      createOpen = false;
      createName = '';
    } catch (err) {
      toggleError = err instanceof Error ? err.message : 'Could not create the playlist.';
    } finally {
      createBusy = false;
    }
  }
</script>

<div class="relative" bind:this={container}>
  <button
    type="button"
    aria-haspopup="true"
    aria-expanded={open}
    onclick={toggleOpen}
    class={[
      'flex items-center gap-1.5 rounded-lg border px-4 py-2 text-xs font-semibold transition',
      isSaved
        ? 'border-blue-500/50 bg-blue-500/10 text-blue-300 hover:bg-blue-500/20'
        : 'border-slate-800 bg-slate-900/70 text-slate-300 hover:bg-slate-800'
    ]}
  >
    {#if isSaved}
      <FolderSolid class="h-4 w-4" />
      Saved
    {:else}
      <FolderOutline class="h-4 w-4" />
      Save
    {/if}
  </button>

  {#if open}
    <div
      class="absolute right-0 top-full z-40 mt-2 w-72 rounded-xl border border-slate-700/80 bg-[#151a26] p-2 shadow-2xl shadow-black/50"
      role="dialog"
      aria-label="Save to playlist"
    >
      <p class="px-2 pb-1.5 pt-1 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-500">
        Save to playlist
      </p>

      {#if loadError}
        <div class="flex items-start gap-2 rounded-lg bg-red-950/40 p-3 text-xs text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <span>{loadError}</span>
        </div>
      {:else if loading || !loaded}
        <div class="flex justify-center py-5">
          <Spinner size="5" />
        </div>
      {:else}
        {#if toggleError}
          <div class="mb-1 flex items-start gap-2 rounded-lg bg-red-950/40 p-2.5 text-xs text-red-300" role="alert">
            <ExclamationCircleOutline class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span>{toggleError}</span>
          </div>
        {/if}

        {#if playlists.length === 0}
          <p class="px-2 py-3 text-center text-xs text-slate-500">You have no playlists yet.</p>
        {:else}
          <ul class="max-h-64 space-y-0.5 overflow-y-auto">
            {#each playlists as playlist (playlist.playlistId)}
              <li>
                <button
                  type="button"
                  role="menuitemcheckbox"
                  aria-checked={membership[playlist.playlistId] === true}
                  disabled={busyPlaylistId !== null}
                  onclick={() => toggleMembership(playlist)}
                  class="flex w-full items-center gap-2.5 rounded-lg px-2 py-2 text-left transition hover:bg-slate-800/70 disabled:opacity-60"
                >
                  <span
                    class={[
                      'grid h-4.5 w-4.5 shrink-0 place-items-center rounded border transition',
                      membership[playlist.playlistId]
                        ? 'border-blue-500 bg-blue-500 text-white'
                        : 'border-slate-600 bg-slate-950/60 text-transparent'
                    ]}
                  >
                    {#if busyPlaylistId === playlist.playlistId}
                      <Spinner size="4" />
                    {:else}
                      <CheckOutline class="h-3 w-3" />
                    {/if}
                  </span>
                  <span class="min-w-0 flex-1">
                    <span class="block truncate text-sm text-slate-200">{playlist.name}</span>
                    <span class="block text-[11px] text-slate-500">
                      {playlist.itemCount} {playlist.itemCount === 1 ? 'item' : 'items'}
                    </span>
                  </span>
                  <ListMusicOutline class="h-3.5 w-3.5 shrink-0 text-slate-600" />
                </button>
              </li>
            {/each}
          </ul>
        {/if}

        <div class="mt-1 border-t border-slate-800 pt-1">
          {#if createOpen}
            <form class="space-y-2 p-2" onsubmit={createAndAdd}>
              <!-- svelte-ignore a11y_autofocus -->
              <input
                type="text"
                bind:value={createName}
                maxlength={255}
                placeholder="Playlist name"
                autofocus
                class="w-full rounded-lg border border-slate-800 bg-slate-950/60 px-3 py-2 text-sm text-slate-200 placeholder:text-slate-600 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
              <div class="flex gap-2">
                <button
                  type="submit"
                  disabled={createName.trim().length === 0 || createBusy}
                  class="flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-blue-500 px-3 py-1.5 text-xs font-semibold text-white transition hover:bg-blue-400 disabled:opacity-50"
                >
                  {#if createBusy}
                    <Spinner size="4" />
                  {/if}
                  Create & save
                </button>
                <button
                  type="button"
                  disabled={createBusy}
                  onclick={() => (createOpen = false)}
                  class="rounded-lg border border-slate-700 px-3 py-1.5 text-xs font-semibold text-slate-300 transition hover:bg-slate-800"
                >
                  Cancel
                </button>
              </div>
            </form>
          {:else}
            <button
              type="button"
              onclick={() => (createOpen = true)}
              class="flex w-full items-center gap-2.5 rounded-lg px-2 py-2 text-left text-sm font-semibold text-slate-300 transition hover:bg-slate-800/70"
            >
              <PlusOutline class="h-4 w-4" />
              New playlist
            </button>
          {/if}
        </div>
      {/if}
    </div>
  {/if}
</div>
