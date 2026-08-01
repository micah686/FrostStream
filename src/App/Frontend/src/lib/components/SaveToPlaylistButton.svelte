<script lang="ts">
  import {
    Check,
    CircleAlert,
    Folder,
    ListMusic,
    Plus
  } from '@lucide/svelte';
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
        ? 'border-primary/50 bg-primary/10 text-primary hover:bg-primary/20'
        : 'border-base-300 bg-base-200/70 text-base-content/80 hover:bg-base-300'
    ]}
  >
    <Folder class="h-4 w-4" />
    {#if isSaved}
      Saved
    {:else}
      Save
    {/if}
  </button>

  {#if open}
    <div
      class="absolute right-0 top-full z-40 mt-2 w-72 rounded-xl border border-base-content/20 bg-base-100 p-2 shadow-2xl shadow-black/50"
      role="dialog"
      aria-label="Save to playlist"
    >
      <p class="px-2 pb-1.5 pt-1 text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/50">
        Save to playlist
      </p>

      {#if loadError}
        <div class="alert alert-error text-xs" role="alert">
          <CircleAlert class="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <span>{loadError}</span>
        </div>
      {:else if loading || !loaded}
        <div class="flex justify-center py-5">
          <span class="loading loading-spinner loading-sm"></span>
        </div>
      {:else}
        {#if toggleError}
          <div class="alert alert-error mb-1 text-xs" role="alert">
            <CircleAlert class="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span>{toggleError}</span>
          </div>
        {/if}

        {#if playlists.length === 0}
          <p class="px-2 py-3 text-center text-xs text-base-content/50">You have no playlists yet.</p>
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
                  class="flex w-full items-center gap-2.5 rounded-lg px-2 py-2 text-left transition hover:bg-base-300/70 disabled:opacity-60"
                >
                  <span
                    class={[
                      'grid h-4.5 w-4.5 shrink-0 place-items-center rounded border transition',
                      membership[playlist.playlistId]
                        ? 'border-primary bg-primary text-primary-content'
                        : 'border-base-content/30 bg-base-200/60 text-transparent'
                    ]}
                  >
                    {#if busyPlaylistId === playlist.playlistId}
                      <span class="loading loading-spinner loading-xs"></span>
                    {:else}
                      <Check class="h-3 w-3" />
                    {/if}
                  </span>
                  <span class="min-w-0 flex-1">
                    <span class="block truncate text-sm text-base-content/90">{playlist.name}</span>
                    <span class="block text-[11px] text-base-content/50">
                      {playlist.itemCount} {playlist.itemCount === 1 ? 'item' : 'items'}
                    </span>
                  </span>
                  <ListMusic class="h-3.5 w-3.5 shrink-0 text-base-content/40" />
                </button>
              </li>
            {/each}
          </ul>
        {/if}

        <div class="mt-1 border-t border-base-300 pt-1">
          {#if createOpen}
            <form class="space-y-2 p-2" onsubmit={createAndAdd}>
              <!-- svelte-ignore a11y_autofocus -->
              <input
                type="text"
                bind:value={createName}
                maxlength={255}
                placeholder="Playlist name"
                autofocus
                class="w-full rounded-lg border border-base-300 bg-base-200/60 px-3 py-2 text-sm text-base-content/90 placeholder:text-base-content/40 focus:border-primary focus:outline-none focus:ring-1 focus:ring-primary"
              />
              <div class="flex gap-2">
                <button
                  type="submit"
                  disabled={createName.trim().length === 0 || createBusy}
                  class="flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-primary px-3 py-1.5 text-xs font-semibold text-primary-content transition hover:bg-primary disabled:opacity-50"
                >
                  {#if createBusy}
                    <span class="loading loading-spinner loading-xs"></span>
                  {/if}
                  Create & save
                </button>
                <button
                  type="button"
                  disabled={createBusy}
                  onclick={() => (createOpen = false)}
                  class="rounded-lg border border-base-content/20 px-3 py-1.5 text-xs font-semibold text-base-content/80 transition hover:bg-base-300"
                >
                  Cancel
                </button>
              </div>
            </form>
          {:else}
            <button
              type="button"
              onclick={() => (createOpen = true)}
              class="flex w-full items-center gap-2.5 rounded-lg px-2 py-2 text-left text-sm font-semibold text-base-content/80 transition hover:bg-base-300/70"
            >
              <Plus class="h-4 w-4" />
              New playlist
            </button>
          {/if}
        </div>
      {/if}
    </div>
  {/if}
</div>
