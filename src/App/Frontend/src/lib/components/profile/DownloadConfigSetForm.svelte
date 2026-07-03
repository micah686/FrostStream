<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { Alert, Button, Checkbox, Input, Label, Select, Spinner, Textarea } from 'flowbite-svelte';
  import {
    ArrowLeftOutline,
    CheckCircleOutline,
    ExclamationCircleOutline,
    PlusOutline
  } from 'flowbite-svelte-icons';
  import {
    createDownloadConfigSet,
    updateDownloadConfigSet,
    type AudioRenditionFormat,
    type DownloadConfigSet,
    type DownloadConfigSetRequest,
    type IgnoreKeyword
  } from '$lib/api/downloadConfigSets';
  import { listOptionPresets, type OptionPreset } from '$lib/api/optionPresets';
  import RangeSlider from '$lib/components/RangeSlider.svelte';

  interface Props {
    mode: 'create' | 'update';
    initial?: DownloadConfigSet | null;
  }

  let { mode, initial = null }: Props = $props();

  const audioFormatOptions = [
    { value: 'Aac', name: 'AAC' },
    { value: 'Opus', name: 'Opus' },
    { value: 'Mp3', name: 'MP3' }
  ];

  let key = $state(untrack(() => initial?.key ?? ''));
  let name = $state(untrack(() => initial?.name ?? ''));
  let description = $state(untrack(() => initial?.description ?? ''));
  let storageKey = $state(untrack(() => initial?.storageKey ?? 'default'));
  let cookieProfileKey = $state(untrack(() => initial?.cookieProfileKey ?? ''));
  let priority = $state(untrack(() => initial?.priority ?? 0));
  let encodeForPlaylist = $state(untrack(() => initial?.encodeForPlaylist ?? false));
  let audioFormat = $state<AudioRenditionFormat>(untrack(() => initial?.audioFormat ?? 'Aac'));
  let fetchComments = $state(untrack(() => initial?.fetchComments ?? false));
  let ignoreKeywordsText = $state(untrack(() => formatIgnoreKeywords(initial?.ignoreKeywords ?? [])));
  let selectedOptionPresetKey = $state(untrack(() => (initial?.ytDlpOptions ? '__existing' : '')));
  let optionPresets = $state<OptionPreset[]>([]);
  let optionPresetsLoading = $state(true);
  let optionPresetsError = $state<string | null>(null);
  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const isUpdate = $derived(mode === 'update');
  const optionPresetItems = $derived([
    { value: '', name: 'None (server defaults)' },
    ...(initial?.ytDlpOptions ? [{ value: '__existing', name: 'Existing options on this config set' }] : []),
    ...optionPresets.map((preset) => ({
      value: preset.key,
      name: preset.description ? `${preset.name} — ${preset.description}` : preset.name
    }))
  ]);

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

  async function save(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    try {
      const request = buildRequest();
      if (isUpdate) {
        await updateDownloadConfigSet(initial!.key, request);
      } else {
        await createDownloadConfigSet(request);
      }
      await goto('/profile');
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Could not save the config set.';
    } finally {
      submitting = false;
    }
  }

  function buildRequest(): DownloadConfigSetRequest {
    const normalizedPriority = Number(priority);
    if (!Number.isInteger(normalizedPriority) || normalizedPriority < 0 || normalizedPriority > 100) {
      throw new Error('Priority must be a whole number from 0 to 100.');
    }

    return {
      key: key.trim(),
      name: name.trim(),
      description: description.trim() || null,
      storageKey: storageKey.trim() || 'default',
      cookieProfileKey: cookieProfileKey.trim() || null,
      ytDlpOptions: selectedYtDlpOptions(),
      ignoreKeywords: parseIgnoreKeywords(ignoreKeywordsText),
      encodeForPlaylist,
      audioFormat,
      priority: normalizedPriority,
      fetchComments
    };
  }

  function selectedYtDlpOptions(): Record<string, unknown> | null {
    if (!selectedOptionPresetKey) {
      return null;
    }
    if (selectedOptionPresetKey === '__existing') {
      return clonePlainOptions(initial?.ytDlpOptions ?? null);
    }
    const preset = optionPresets.find((item) => item.key === selectedOptionPresetKey);
    if (!preset) {
      throw new Error('Selected option preset could not be found.');
    }
    return clonePlainOptions(preset.ytDlpOptions);
  }

  function parseIgnoreKeywords(value: string): IgnoreKeyword[] {
    return value
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
      .map((line) => {
        if (line.toLowerCase().startsWith('regex:')) {
          return { pattern: line.slice(6).trim(), matchType: 'Regex' as const };
        }
        if (line.toLowerCase().startsWith('substring:')) {
          return { pattern: line.slice(10).trim(), matchType: 'Substring' as const };
        }
        return { pattern: line, matchType: 'Substring' as const };
      })
      .filter((keyword) => keyword.pattern.length > 0);
  }

  function formatIgnoreKeywords(keywords: IgnoreKeyword[]): string {
    return keywords
      .map((keyword) => `${keyword.matchType === 'Regex' ? 'regex:' : ''}${keyword.pattern}`)
      .join('\n');
  }

  function clonePlainOptions(value: unknown): Record<string, unknown> | null {
    if (!value || typeof value !== 'object') {
      return null;
    }

    try {
      return JSON.parse(JSON.stringify($state.snapshot(value))) as Record<string, unknown>;
    } catch {
      return null;
    }
  }
</script>

<form onsubmit={save} class="space-y-5">
  <div class="grid gap-5 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
    <div>
      <Label for="config-key" class="mb-2 text-sm font-medium text-slate-300">Key</Label>
      <Input
        id="config-key"
        required
        pattern={'[a-z0-9-]{2,100}'}
        minlength={2}
        maxlength={100}
        disabled={isUpdate}
        bind:value={key}
        placeholder="mobile-friendly"
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500! disabled:opacity-60"
      />
      <p class="mt-1.5 text-xs text-slate-600">Lowercase letters, numbers, and hyphens.</p>
    </div>

    <div>
      <Label for="config-name" class="mb-2 text-sm font-medium text-slate-300">Name</Label>
      <Input
        id="config-name"
        required
        maxlength={255}
        bind:value={name}
        placeholder="Mobile-friendly"
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>
  </div>

  <div>
    <Label for="config-description" class="mb-2 text-sm font-medium text-slate-300">Description</Label>
    <Textarea
      id="config-description"
      rows={3}
      maxlength={2000}
      bind:value={description}
      placeholder="720p H.264 with fast transcode settings"
      class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
    />
  </div>

  <div class="grid gap-5 sm:grid-cols-2">
    <div>
      <Label for="storage-key" class="mb-2 text-sm font-medium text-slate-300">Storage key</Label>
      <Input
        id="storage-key"
        bind:value={storageKey}
        placeholder="default"
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>

    <div>
      <Label for="cookie-profile-key" class="mb-2 text-sm font-medium text-slate-300">Cookie profile key</Label>
      <Input
        id="cookie-profile-key"
        pattern={'[a-z0-9-]{2,100}'}
        bind:value={cookieProfileKey}
        placeholder="optional"
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>
  </div>

  <div class="grid gap-5 sm:grid-cols-2">
    <div>
      <Label for="priority" class="mb-2 text-sm font-medium text-slate-300">
        Priority <span class="font-normal text-slate-500">({priority})</span>
      </Label>
      <RangeSlider id="priority" min={0} max={100} step={1} bind:value={priority} />
    </div>

    <div>
      <Label for="audio-format" class="mb-2 text-sm font-medium text-slate-300">Playlist audio format</Label>
      <Select
        id="audio-format"
        items={audioFormatOptions}
        bind:value={audioFormat}
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
      />
    </div>
  </div>

  <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-slate-800/70 pt-5">
    <Checkbox bind:checked={encodeForPlaylist} class="text-sm text-slate-300">
      Encode audio rendition for playlists
    </Checkbox>
    <Checkbox bind:checked={fetchComments} class="text-sm text-slate-300">
      Fetch comments
    </Checkbox>
  </div>

  <div>
    <Label for="ignore-keywords" class="mb-2 text-sm font-medium text-slate-300">Ignore keywords</Label>
    <Textarea
      id="ignore-keywords"
      rows={5}
      bind:value={ignoreKeywordsText}
      placeholder={'shorts\nregex:\\btrailer\\b'}
      class="font-mono! border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
    />
    <p class="mt-1.5 text-xs text-slate-600">One per line. Prefix with <code>regex:</code> for regex matching.</p>
  </div>

  <div>
    <Label for="option-preset" class="mb-2 text-sm font-medium text-slate-300">Option preset</Label>
    {#if optionPresetsLoading}
      <div class="flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-950/60 px-3 py-2 text-sm text-slate-500">
        <Spinner size="4" />
        Loading option presets...
      </div>
    {:else}
      <Select
        id="option-preset"
        items={optionPresetItems}
        bind:value={selectedOptionPresetKey}
        class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
      />
    {/if}
    <p class="mt-1.5 text-xs text-slate-600">
      Pick a saved option preset to apply its yt-dlp options to this config set. Choose none to use server defaults.
    </p>
    {#if optionPresetsError}
      <Alert color="red" class="mt-3 border-red-900/60! bg-red-950/35! text-red-300!">
        {optionPresetsError}
      </Alert>
    {:else if !optionPresetsLoading && optionPresets.length === 0 && !initial?.ytDlpOptions}
      <Alert color="gray" class="mt-3 border-slate-800! bg-slate-950/35! text-slate-400!">
        No option presets exist yet. Create one from Profile → Option presets if this config set needs custom yt-dlp options.
      </Alert>
    {/if}
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
      href="/profile"
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
      {isUpdate ? 'Save changes' : 'Create config set'}
    </Button>
  </div>
</form>
