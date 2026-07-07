<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Spinner } from 'flowbite-svelte';
  import { ExclamationCircleOutline } from 'flowbite-svelte-icons';
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
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-blue-400">Profile</p>
    <h1 id="option-preset-title" class="mt-2 text-2xl font-bold tracking-tight text-slate-100">
      {preset?.name ?? 'Option preset'}
    </h1>
    <p class="mt-2 text-sm text-slate-400">
      View and update this stored set of yt-dlp options.
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
        href="/profile/option-presets"
        color="dark"
        class="mt-4 border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
      >
        Back to profile
      </Button>
    </div>
  {:else if preset}
    <div class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
      <OptionPresetForm mode="update" initial={preset} />
    </div>
  {/if}
</section>
