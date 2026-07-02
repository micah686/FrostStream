<script lang="ts">
  import DownloadConfigSetForm from '$lib/components/profile/DownloadConfigSetForm.svelte';
</script>

<svelte:head>
  <title>New config set · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="config-set-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-blue-400">Profile</p>
    <h1 id="config-set-title" class="mt-2 text-2xl font-bold tracking-tight text-slate-100">New config set</h1>
    <p class="mt-2 text-sm text-slate-400">
      Create a reusable download configuration for future downloads, playlists, and channel imports.
    </p>
  </div>

  <div class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
    <DownloadConfigSetForm mode="create" />
  </div>
</section>
