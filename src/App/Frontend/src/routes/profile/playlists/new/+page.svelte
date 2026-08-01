<script lang="ts">
  import { goto } from '$app/navigation';
  import { ArrowLeft, CircleAlert } from '@lucide/svelte';
  import { createUserPlaylist } from '$lib/api/userPlaylists';

  let name = $state('');
  let description = $state('');
  let saving = $state(false);
  let error = $state<string | null>(null);
  const formValid = $derived(name.trim().length > 0 && name.trim().length <= 255);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!formValid || saving) return;

    saving = true;
    error = null;
    try {
      const playlist = await createUserPlaylist({ name: name.trim(), description: description.trim() || null });
      await goto(`/profile/playlists/${encodeURIComponent(playlist.playlistId)}`);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not save the playlist.';
    } finally {
      saving = false;
    }
  }
</script>

<svelte:head><title>New playlist · FrostStream</title></svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="playlist-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="playlist-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">New playlist</h1>
    <p class="mt-2 text-sm text-base-content/60">Create a private playlist for archived media on this server.</p>
  </div>

  <form class="card space-y-4 border border-base-300 bg-base-100 p-5 sm:p-6" onsubmit={save}>
    {#if error}
      <div class="alert alert-error text-sm" role="alert"><CircleAlert class="mt-0.5 h-4 w-4 shrink-0" /><span>{error}</span></div>
    {/if}
    <div>
      <label class="label mb-1.5 text-xs" for="playlist-name">Name</label>
      <input class="input w-full" id="playlist-name" bind:value={name} maxlength={255} placeholder="Watch later, favourites…" />
    </div>
    <div>
      <label class="label mb-1.5 text-xs" for="playlist-description">Description (optional)</label>
      <textarea class="textarea w-full" id="playlist-description" bind:value={description} maxlength={2048} rows={3} placeholder="What belongs in this playlist?"></textarea>
    </div>
    <div class="flex flex-wrap justify-between gap-2">
      <a class="btn btn-sm btn-neutral" href="/profile/playlists"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a>
      <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={!formValid || saving}>
        {#if saving}<span class="loading loading-spinner loading-xs mr-1.5"></span>{/if}Create playlist
      </button>
    </div>
  </form>
</section>
