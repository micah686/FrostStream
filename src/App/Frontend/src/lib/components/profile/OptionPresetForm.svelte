<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { Button, Input, Label, Spinner, Textarea } from 'flowbite-svelte';
  import {
    ArrowLeftOutline,
    CheckCircleOutline,
    ExclamationCircleOutline,
    PlusOutline
  } from 'flowbite-svelte-icons';
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
      <Label for="preset-key" class="mb-2 text-sm font-medium text-slate-300">Key</Label>
      <Input
        id="preset-key"
        required
        pattern={'[a-z0-9-]{2,100}'}
        minlength={2}
        maxlength={100}
        disabled={isUpdate}
        bind:value={key}
        placeholder="archive-1080p"
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500! disabled:opacity-60"
      />
      <p class="mt-1.5 text-xs text-slate-600">Lowercase letters, numbers, and hyphens.</p>
    </div>

    <div>
      <Label for="preset-name" class="mb-2 text-sm font-medium text-slate-300">Name</Label>
      <Input
        id="preset-name"
        required
        maxlength={255}
        bind:value={name}
        placeholder="Archive 1080p"
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>
  </div>

  <div>
    <Label for="preset-description" class="mb-2 text-sm font-medium text-slate-300">Description</Label>
    <Textarea
      id="preset-description"
      rows={3}
      maxlength={2000}
      bind:value={description}
      placeholder="Best quality up to 1080p with embedded metadata"
      class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
    />
  </div>

  <div>
    <h2 class="mb-3 border-t border-slate-800/70 pt-5 text-sm font-semibold text-slate-200">
      Download options
    </h2>
    <YtDlpOptionsEditor bind:value={ytDlpOptions} />
  </div>

  {#if submitError}
    <div
      class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/40 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{submitError}</span>
    </div>
  {/if}

  <div class="flex flex-col-reverse gap-3 border-t border-slate-800/70 pt-5 sm:flex-row sm:justify-between">
    <Button
      href={profileReturnHref}
      color="dark"
      class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
    >
      <ArrowLeftOutline class="mr-1.5 h-4 w-4" />
      Back
    </Button>
    <Button
      type="submit"
      color="blue"
      disabled={submitting}
      class="border-0! bg-blue-500! px-5! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
    >
      {#if submitting}
        <Spinner size="4" class="mr-2" />
      {:else if isUpdate}
        <CheckCircleOutline class="mr-1.5 h-4 w-4" />
      {:else}
        <PlusOutline class="mr-1.5 h-4 w-4" />
      {/if}
      {isUpdate ? 'Save changes' : 'Create preset'}
    </Button>
  </div>
</form>
