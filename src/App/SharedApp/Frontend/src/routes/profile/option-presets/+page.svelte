<script lang="ts">
  import { onMount } from 'svelte';
  import {
    CircleAlert,
    Eye,
    Plus,
    SlidersVertical,
    Trash2
  } from '@lucide/svelte';
  import {
    deleteOptionPreset,
    listOptionPresets,
    type OptionPreset
  } from '$lib/api/optionPresets';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

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

<section class="card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 class="text-base font-bold text-base-content">Option presets</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Named sets of yt-dlp options. Reference one from a preset-based download request to reuse the same options.
      </p>
    </div>
    <a class="btn btn-sm btn-neutral shrink-0" href="/profile/option-presets/new">
      <Plus class="mr-1.5 h-3.5 w-3.5" />
      New option preset
    </a>
  </div>

  {#if optionPresetsError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{optionPresetsError}</span>
    </div>
  {/if}

  {#if optionPresetsLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if optionPresets.length === 0}
    <div class="mt-5 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/30 p-8 text-center">
      <SlidersVertical class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No option presets yet</p>
      <p class="mt-1 text-sm text-base-content/50">Create one to reuse yt-dlp options across downloads.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each optionPresets as preset (preset.key)}
        <article
          class="card flex min-h-[3.95rem] flex-col gap-3 border-[length:var(--border)] border-base-300 bg-base-100 p-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="flex min-w-0 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-field bg-base-300/70 text-primary">
              <SlidersVertical class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-base-content">{preset.name}</h3>
                <span class="badge badge-sm badge-accent text-[10px] text-accent-content">
                  {preset.key}
                </span>
              </div>
              <p class="mt-0.5 truncate text-xs text-base-content/60">
                {preset.description || presetSummary(preset)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <a
              href={`/profile/option-presets/${encodeURIComponent(preset.key)}`}
              class="btn btn-sm btn-neutral text-xs"
              aria-label={`Edit option preset ${preset.name}`}
            >
              <Eye class="mr-1.5 h-4 w-4" />
              Edit
            </a>
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Delete option preset"
              aria-label={`Delete option preset ${preset.name}`}
              disabled={deletingPresetKey === preset.key}
              onclick={() => {
                presetPendingDelete = preset;
                presetDeleteModalOpen = true;
              }}
            >
              {#if deletingPresetKey === preset.key}
                <span class="loading loading-spinner loading-xs mr-1.5"></span>
              {:else}
                <Trash2 class="mr-1.5 h-4 w-4" />
              {/if}
              Delete
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}

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
