<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Spinner } from 'flowbite-svelte';
  import { ExclamationCircleOutline } from 'flowbite-svelte-icons';
  import DownloadConfigSetForm from '$lib/components/profile/DownloadConfigSetForm.svelte';
  import { getDownloadConfigSet, type DownloadConfigSet } from '$lib/api/downloadConfigSets';

  let { params } = $props();

  let config = $state<DownloadConfigSet | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  onMount(() => {
    void loadConfigSet();
  });

  async function loadConfigSet() {
    loading = true;
    loadError = null;
    try {
      config = await getDownloadConfigSet(params.key);
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the config set.';
    } finally {
      loading = false;
    }
  }
</script>

<svelte:head>
  <title>{config?.name ?? 'Config set'} · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="config-set-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-blue-400">Profile</p>
    <h1 id="config-set-title" class="mt-2 text-2xl font-bold tracking-tight text-slate-100">
      {config?.name ?? 'Config set'}
    </h1>
    <p class="mt-2 text-sm text-slate-400">
      View and update this reusable download configuration.
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
        href="/profile/config-sets"
        color="dark"
        class="mt-4 border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
      >
        Back to profile
      </Button>
    </div>
  {:else if config}
    <div class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
      <DownloadConfigSetForm mode="update" initial={config} />
    </div>
  {/if}
</section>
