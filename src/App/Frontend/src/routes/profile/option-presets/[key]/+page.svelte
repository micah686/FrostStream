<script lang="ts">
  import { onMount } from 'svelte';
  import { CircleAlert } from '@lucide/svelte';
  import OptionPresetForm from '$lib/components/profile/OptionPresetForm.svelte';
  import { getOptionPreset, type OptionPreset } from '$lib/api/optionPresets';

  let { params } = $props();

  let preset = $state<OptionPreset | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  onMount(() => {
    void loadPreset();
  });

  async function loadPreset() {
    loading = true;
    loadError = null;
    try {
      preset = await getOptionPreset(params.key);
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the option preset.';
    } finally {
      loading = false;
    }
  }
</script>

<svelte:head>
  <title>{preset?.name ?? 'Option preset'} · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="option-preset-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="option-preset-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">
      {preset?.name ?? 'Option preset'}
    </h1>
    <p class="mt-2 text-sm text-base-content/60">
      View and update this stored set of yt-dlp options.
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
      <a class="btn btn-sm btn-neutral mt-4 text-xs" href="/profile/option-presets">
        Back to profile
      </a>
    </div>
  {:else if preset}
    <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
      <OptionPresetForm mode="update" initial={preset} />
    </div>
  {/if}
</section>
