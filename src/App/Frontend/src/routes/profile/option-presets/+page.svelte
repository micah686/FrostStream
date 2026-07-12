<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Spinner } from 'flowbite-svelte';
  import {
    AdjustmentsVerticalOutline,
    ExclamationCircleOutline,
    EyeOutline,
    PlusOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import {
    deleteOptionPreset,
    listOptionPresets,
    type OptionPreset
  } from '$lib/api/optionPresets';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

  let { data } = $props();

  const sessionLabel = $derived(data.singleUser ? 'local profile' : 'FrostStream account');

  let optionPresets = $state<OptionPreset[]>([]);
  let optionPresetsLoading = $state(true);
  let optionPresetsError = $state<string | null>(null);
  let deletingPresetKey = $state<string | null>(null);
  let presetPendingDelete = $state<OptionPreset | null>(null);
  let presetDeleteModalOpen = $state(false);

  onMount(() => {
    void loadOptionPresets();
  });

  async function loadOptionPresets() {
    optionPresetsLoading = true;
    optionPresetsError = null;
    try {
      optionPresets = await listOptionPresets();
    } catch (err) {
      optionPresetsError = err instanceof Error ? err.message : 'Could not load option presets.';
    } finally {
      optionPresetsLoading = false;
    }
  }

  async function deletePreset(preset: OptionPreset) {
    deletingPresetKey = preset.key;
    optionPresetsError = null;
    try {
      await deleteOptionPreset(preset.key);
      optionPresets = optionPresets.filter((item) => item.key !== preset.key);
      presetPendingDelete = null;
    } catch (err) {
      optionPresetsError = err instanceof Error ? err.message : 'Could not delete the option preset.';
    } finally {
      deletingPresetKey = null;
    }
  }

  function presetSummary(preset: OptionPreset): string {
    const optionCount = Object.keys(preset.ytDlpOptions ?? {}).length;
    return [
      `${optionCount} ${optionCount === 1 ? 'option' : 'options'}`,
      preset.createdAt ? `created ${new Date(preset.createdAt).toLocaleDateString()}` : null,
      preset.lastUpdated ? `updated ${new Date(preset.lastUpdated).toLocaleDateString()}` : null
    ].filter(Boolean).join(' · ');
  }
</script>

<section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
  <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 class="text-base font-bold text-slate-100">Option presets</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Named sets of yt-dlp options. Reference one from a preset-based download request to reuse the same options.
      </p>
    </div>
    <Badge rounded color="gray" class="w-fit bg-slate-800! px-2.5! py-1! text-[10px]! text-slate-400!">
      {sessionLabel}
    </Badge>
  </div>

  {#if optionPresetsError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{optionPresetsError}</span>
    </div>
  {/if}

  {#if optionPresetsLoading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if optionPresets.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <AdjustmentsVerticalOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No option presets yet</p>
      <p class="mt-1 text-sm text-slate-500">Create one to reuse yt-dlp options across downloads.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each optionPresets as preset (preset.key)}
        <article
          class="flex min-h-[3.95rem] flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="flex min-w-0 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
              <AdjustmentsVerticalOutline class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-slate-100">{preset.name}</h3>
                <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                  {preset.key}
                </span>
              </div>
              <p class="mt-0.5 truncate text-xs text-slate-400">
                {preset.description || presetSummary(preset)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <a
              href={`/profile/option-presets/${encodeURIComponent(preset.key)}`}
              class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
              aria-label={`Edit option preset ${preset.name}`}
            >
              <EyeOutline class="h-4 w-4" />
              Edit
            </a>
            <button
              type="button"
              class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
              title="Delete option preset"
              aria-label={`Delete option preset ${preset.name}`}
              disabled={deletingPresetKey === preset.key}
              onclick={() => {
                presetPendingDelete = preset;
                presetDeleteModalOpen = true;
              }}
            >
              {#if deletingPresetKey === preset.key}
                <Spinner size="4" />
              {:else}
                <TrashBinOutline class="h-4 w-4" />
              {/if}
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}

  <div class="mt-4">
    <Button
      href="/profile/option-presets/new"
      color="dark"
      class="border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
    >
      <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
      New option preset
    </Button>
  </div>
</section>

<ConfirmDeleteModal
  bind:open={presetDeleteModalOpen}
  title="Delete option preset"
  message={presetPendingDelete ? `Delete option preset "${presetPendingDelete.name}"? This will not affect existing jobs.` : ''}
  confirmLabel="Delete preset"
  onConfirm={async () => {
    if (presetPendingDelete) {
      await deletePreset(presetPendingDelete);
    }
  }}
/>
