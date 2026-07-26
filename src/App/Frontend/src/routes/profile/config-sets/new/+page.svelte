<script lang="ts">
  import DownloadConfigSetForm from '$lib/components/profile/DownloadConfigSetForm.svelte';
</script>

<svelte:head>
  <title>New config set · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="config-set-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="config-set-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">New config set</h1>
    <p class="mt-2 text-sm text-base-content/60">
      Create a reusable download configuration for future downloads, playlists, and channel imports.
    </p>
  </div>

  <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
    <DownloadConfigSetForm mode="create" />
  </div>
</section>
