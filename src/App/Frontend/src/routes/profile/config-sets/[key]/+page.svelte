<script lang="ts">
  import { onMount } from 'svelte';
  import { CircleAlert } from '@lucide/svelte';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
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

<UnderDevelopmentBanner />

<section class="mx-auto max-w-4xl" aria-labelledby="config-set-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="config-set-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">
      {config?.name ?? 'Config set'}
    </h1>
    <p class="mt-2 text-sm text-base-content/60">
      View and update this reusable download configuration.
    </p>
  </div>

  {#if loading}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if loadError}
    <div class="rounded-2xl border border-error/30 bg-error/10 p-5 text-sm text-error" role="alert">
      <div class="flex items-start gap-3">
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{loadError}</span>
      </div>
      <a class="btn btn-sm btn-neutral mt-4 text-xs" href="/profile/config-sets">
        Back to profile
      </a>
    </div>
  {:else if config}
    <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
      <DownloadConfigSetForm mode="update" initial={config} />
    </div>
  {/if}
</section>
