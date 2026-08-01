<script lang="ts">
  import { onMount } from 'svelte';
  import { CircleAlert, ListMusic, Play, RefreshCw } from '@lucide/svelte';
  import { listProviderPlaylistLibrary, type ProviderPlaylistLibraryItem } from '$lib/api/playlists';
  import { getUserPlaylist, listUserPlaylists, type UserPlaylist } from '$lib/api/userPlaylists';
  import { accentFor } from '$lib/media';

  interface UserPlaylistCard {
    playlist: UserPlaylist;
    firstGuid: string | null;
  }

  interface ProviderPlaylistCard {
    playlist: ProviderPlaylistLibraryItem;
  }

  let userPlaylistCards = $state<UserPlaylistCard[]>([]);
  let providerPlaylistCards = $state<ProviderPlaylistCard[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  onMount(() => {
    void loadPlaylists();
  });

  async function loadPlaylists() {
    loading = true;
    loadError = null;
    try {
      const [userList, providerList] = await Promise.all([listUserPlaylists(), listProviderPlaylistLibrary()]);
      const [userDetails] = await Promise.all([
        Promise.all(userList.map((playlist) => getUserPlaylist(playlist.playlistId).catch(() => playlist))),
      ]);

      userPlaylistCards = userDetails.map((playlist) => ({
        playlist,
        firstGuid: playlist.items?.[0]?.mediaGuid ?? null
      }));
      providerPlaylistCards = providerList.map((playlist) => ({ playlist }));
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load playlists.';
    } finally {
      loading = false;
    }
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }

  function providerTitle(playlist: ProviderPlaylistLibraryItem): string {
    return playlist.title?.trim() || 'Untitled provider playlist';
  }
</script>

<svelte:head>
  <title>Playlists · FrostStream</title>
</svelte:head>

<section aria-labelledby="playlists-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="playlists-title" class="text-2xl font-bold tracking-tight text-base-content">Playlists</h1>
      <p class="mt-1 text-sm text-base-content/50">Your library playlists and provider playlists</p>
    </div>
    <div class="flex items-center gap-2">
      <button class="btn btn-sm btn-neutral text-xs" onclick={loadPlaylists} disabled={loading}>
        {#if loading}<span class="loading loading-spinner loading-xs mr-1.5"></span>{:else}<RefreshCw class="mr-1.5 h-4 w-4" />{/if}
        Refresh
      </button>
    </div>
  </div>

  {#if loadError}
    <div class="alert alert-error mt-5 text-sm" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {/if}

  {#if loading && userPlaylistCards.length === 0 && providerPlaylistCards.length === 0}
    <div class="mt-16 flex justify-center"><span class="loading loading-spinner loading-md"></span></div>
  {:else}
    <section class="mt-8" aria-labelledby="user-created-title">
      <h2 id="user-created-title" class="text-lg font-bold text-base-content">User-created playlists</h2>
      {#if userPlaylistCards.length === 0}
        <p class="mt-4 rounded-xl border border-base-300/80 bg-base-200/40 p-8 text-center text-sm text-base-content/50">
          No user-created playlists yet. Create one from your profile or save a video to a playlist.
        </p>
      {:else}
        <div class="mt-4 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
          {#each userPlaylistCards as card (card.playlist.playlistId)}
            <article class="group min-w-0">
              <a
                href={card.firstGuid ? `/watch/${card.firstGuid}?ulist=${encodeURIComponent(card.playlist.playlistId)}` : `/profile/playlists/${encodeURIComponent(card.playlist.playlistId)}`}
                class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.playlist.playlistId)} shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
                aria-label={`Open playlist ${card.playlist.name}`}
              >
                <ListMusic class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/15" />
                {#if card.firstGuid}<img src={`/api/media/watch/${card.firstGuid}/thumbnail`} alt="" loading="lazy" decoding="async" class="absolute inset-0 h-full w-full object-cover" onerror={hideBrokenImage} />{/if}
                <span class="absolute bottom-0 left-0 right-0 flex items-center justify-between bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-white/90 backdrop-blur-sm">
                  <span class="flex items-center gap-1.5"><ListMusic class="h-3.5 w-3.5" /> Playlist</span>
                  {card.playlist.itemCount} {card.playlist.itemCount === 1 ? 'video' : 'videos'}
                </span>
                {#if card.firstGuid}<span class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-neutral text-neutral-content opacity-0 shadow-xl transition group-hover:scale-100 group-hover:opacity-100"><Play class="ml-0.5 h-5 w-5" /></span>{/if}
              </a>
              <h3 class="mt-3 line-clamp-2 px-1 text-sm font-semibold leading-snug text-base-content/90">{card.playlist.name}</h3>
            </article>
          {/each}
        </div>
      {/if}
    </section>

    <section class="mt-10" aria-labelledby="provider-playlists-title">
      <h2 id="provider-playlists-title" class="text-lg font-bold text-base-content">Provider playlists</h2>
      {#if providerPlaylistCards.length === 0}
        <p class="mt-4 rounded-xl border border-base-300/80 bg-base-200/40 p-8 text-center text-sm text-base-content/50">
          No provider playlists yet. Download a playlist from the Download page to add one to your library.
        </p>
      {:else}
        <div class="mt-4 grid gap-5 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5">
          {#each providerPlaylistCards as card (card.playlist.playlistId)}
            <article class="group min-w-0">
              {#if card.playlist.firstMediaGuid}
                <a href={`/watch/${card.playlist.firstMediaGuid}?list=${encodeURIComponent(card.playlist.playlistId)}`} class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.playlist.playlistId)} shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`} aria-label={`Open playlist ${providerTitle(card.playlist)}`}>
                  <ListMusic class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/15" />
                  <img src={`/api/media/watch/${card.playlist.firstMediaGuid}/thumbnail`} alt="" loading="lazy" decoding="async" class="absolute inset-0 h-full w-full object-cover" onerror={hideBrokenImage} />
                  <span class="absolute bottom-0 left-0 right-0 flex items-center justify-between bg-black/60 px-3 py-1.5 text-[11px] font-semibold text-white/90 backdrop-blur-sm"><span class="flex items-center gap-1.5"><ListMusic class="h-3.5 w-3.5" /> Playlist</span>{card.playlist.itemCount} {card.playlist.itemCount === 1 ? 'video' : 'videos'}</span>
                  <span class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-neutral text-neutral-content opacity-0 shadow-xl transition group-hover:scale-100 group-hover:opacity-100"><Play class="ml-0.5 h-5 w-5" /></span>
                </a>
              {:else}
                <div class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(card.playlist.playlistId)} opacity-70`}><ListMusic class="absolute left-1/2 top-1/2 h-10 w-10 -translate-x-1/2 -translate-y-1/2 text-white/20" /></div>
              {/if}
              <h3 class="mt-3 line-clamp-2 px-1 text-sm font-semibold leading-snug text-base-content/90">{providerTitle(card.playlist)}</h3>
            </article>
          {/each}
        </div>
      {/if}
    </section>
  {/if}
</section>
