<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import {
    ArrowLeft,
    CircleAlert,
    CircleCheck,
    Plus
  } from '@lucide/svelte';
  import {
    createOptionPreset,
    updateOptionPreset,
    type OptionPreset
  } from '$lib/api/optionPresets';
  import YtDlpOptionsEditor from './YtDlpOptionsEditor.svelte';

  interface Props {
    mode: 'create' | 'update';
    initial?: OptionPreset | null;
  }

  let { mode, initial = null }: Props = $props();

  let key = $state(untrack(() => initial?.key ?? ''));
  let name = $state(untrack(() => initial?.name ?? ''));
  let description = $state(untrack(() => initial?.description ?? ''));
  let ytDlpOptions = $state<Record<string, unknown>>(
    untrack(() => clonePlainOptions(initial?.ytDlpOptions))
  );
  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const isUpdate = $derived(mode === 'update');
  const profileReturnHref = '/profile/option-presets';

  function clonePlainOptions(value: unknown): Record<string, unknown> {
    if (!value || typeof value !== 'object') {
      return {};
    }

    try {
      return JSON.parse(JSON.stringify($state.snapshot(value))) as Record<string, unknown>;
    } catch {
      return {};
    }
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    try {
      if (isUpdate) {
        await updateOptionPreset(initial!.key, {
          name: name.trim(),
          description: description.trim() || null,
          ytDlpOptions
        });
      } else {
        await createOptionPreset({
          key: key.trim(),
          name: name.trim(),
          description: description.trim() || null,
          ytDlpOptions
        });
      }
      await goto(profileReturnHref);
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Could not save the option preset.';
    } finally {
      submitting = false;
    }
  }

</script>

<form onsubmit={save} class="space-y-5">
  <div class="grid gap-5 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
    <div>
      <label class="label mb-2 text-sm" for="preset-key">Key</label>
      <input class="input w-full text-sm" id="preset-key" required
         pattern={'[a-z0-9\\-]{2,100}'} minlength={2} maxlength={100} disabled={isUpdate} bind:value={key} placeholder="archive-1080p" />
      <p class="mt-1.5 text-xs text-base-content/40">Lowercase letters, numbers, and hyphens.</p>
    </div>

    <div>
      <label class="label mb-2 text-sm" for="preset-name">Name</label>
      <input class="input w-full text-sm" id="preset-name" required
         maxlength={255} bind:value={name} placeholder="Archive 1080p" />
    </div>
  </div>

  <div>
    <label class="label mb-2 text-sm" for="preset-description">Description</label>
    <textarea class="textarea w-full text-sm" id="preset-description" rows={3} maxlength={2000} bind:value={description} placeholder="Best quality up to 1080p with embedded metadata"></textarea>
  </div>

  <div>
    <h2 class="mb-3 border-t border-base-300/70 pt-5 text-sm font-semibold text-base-content/90">
      Download options
    </h2>
    <YtDlpOptionsEditor bind:value={ytDlpOptions} />
  </div>

  {#if submitError}
    <div
      class="alert alert-error text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{submitError}</span>
    </div>
  {/if}

  <div class="flex flex-col-reverse gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:justify-between">
    <a class="btn btn-sm btn-neutral text-xs" href={profileReturnHref}>
      <ArrowLeft class="mr-1.5 h-4 w-4" />
      Back
    </a>
    <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={submitting}>
      {#if submitting}
        <span class="loading loading-spinner loading-xs mr-2"></span>
      {:else if isUpdate}
        <CircleCheck class="mr-1.5 h-4 w-4" />
      {:else}
        <Plus class="mr-1.5 h-4 w-4" />
      {/if}
      {isUpdate ? 'Save changes' : 'Create preset'}
    </button>
  </div>
</form>
