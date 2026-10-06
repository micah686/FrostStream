<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { Select } from '$lib/components/ui';
  import {
    ArrowLeft,
    CircleAlert,
    CircleCheck,
    Plus
  } from '@lucide/svelte';
  import {
    createDownloadConfigSet,
    updateDownloadConfigSet,
    type DownloadConfigSet,
    type DownloadConfigSetRequest,
    type IgnoreKeyword
  } from '$lib/api/downloadConfigSets';
  import { listOptionPresets, type OptionPreset } from '$lib/api/optionPresets';
  import { listStorage } from '$lib/api/storage';
  import { listCookieProfiles } from '$lib/api/cookies';

  interface Props {
    mode: 'create' | 'update';
    initial?: DownloadConfigSet | null;
  }

  let { mode, initial = null }: Props = $props();

  interface SelectItem {
    value: string;
    name: string;
  }

  let key = $state(untrack(() => initial?.key ?? ''));
  let name = $state(untrack(() => initial?.name ?? ''));
  let description = $state(untrack(() => initial?.description ?? ''));
  let storageKey = $state(untrack(() => initial?.storageKey ?? 'default'));
  let cookieProfileKey = $state(untrack(() => initial?.cookieProfileKey ?? ''));
  let workerTag = $state(untrack(() => initial?.workerTag ?? ''));
  let priority = $state(untrack(() => initial?.priority ?? 0));
  let ignoreKeywordsText = $state(untrack(() => formatIgnoreKeywords(initial?.ignoreKeywords ?? [])));
  let selectedOptionPresetKey = $state(untrack(() => (initial?.ytDlpOptions ? '__existing' : '')));
  let optionPresets = $state<OptionPreset[]>([]);
  let optionPresetsLoading = $state(true);
  let optionPresetsError = $state<string | null>(null);
  let storageOptions = $state<SelectItem[]>([{ value: 'default', name: 'default' }]);
  let storageLoadFailed = $state(false);
  let cookieOptions = $state<SelectItem[]>([]);
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
  const cookieProfileItems = $derived([{ value: '', name: 'None' }, ...cookieOptions]);

  onMount(() => {
    void loadOptionPresets();
    void loadStorageTargets();
    void loadCookieProfiles();
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

  async function loadStorageTargets() {
    try {
      const items = await listStorage();
      if (items.length > 0) {
        storageOptions = items.map((item) => ({
          value: item.key,
          name: item.description ? `${item.key} — ${item.description}` : item.key
        }));
        if (!storageOptions.some((option) => option.value === storageKey)) {
          storageKey = storageOptions[0].value;
        }
      }
    } catch {
      // Fall back to a free-text storage key input when the list is unavailable.
      storageLoadFailed = true;
    }
  }

  async function loadCookieProfiles() {
    try {
      const items = await listCookieProfiles();
      cookieOptions = items.map((item) => ({
        value: item.profileKey,
        name: item.displayName || item.site ? `${item.profileKey} (${item.displayName ?? item.site})` : item.profileKey
      }));
    } catch {
      // Cookie profiles are optional; config sets work without them.
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
      await goto('/profile/config-sets');
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
      workerTag: workerTag.trim() || null,
      ytDlpOptions: selectedYtDlpOptions(),
      ignoreKeywords: parseIgnoreKeywords(ignoreKeywordsText),
      priority: normalizedPriority
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
      <label class="label mb-2 text-sm" for="config-key">Key</label>
      <input class="input w-full text-sm" id="config-key" required
         pattern={'[a-z0-9\\-]{2,100}'} minlength={2} maxlength={100} disabled={isUpdate} bind:value={key} placeholder="mobile-friendly" />
      <p class="mt-1.5 text-xs text-base-content/40">Lowercase letters, numbers, and hyphens.</p>
    </div>

    <div>
      <label class="label mb-2 text-sm" for="config-name">Name</label>
      <input class="input w-full text-sm" id="config-name" required
         maxlength={255} bind:value={name} placeholder="Mobile-friendly" />
    </div>
  </div>

  <div>
    <label class="label mb-2 text-sm" for="config-description">Description</label>
    <textarea class="textarea w-full text-sm" id="config-description" rows={3} maxlength={2000} bind:value={description} placeholder="720p H.264 with fast transcode settings"></textarea>
  </div>

  <div class="grid gap-5 sm:grid-cols-2">
    <div>
      <label class="label mb-2 text-sm" for="storage-key">Storage key</label>
      {#if storageLoadFailed}
        <input class="input w-full text-sm" id="storage-key" bind:value={storageKey} placeholder="default" />
      {:else}
        <Select id="storage-key" items={storageOptions} bind:value={storageKey} class="text-sm" />
      {/if}
    </div>

    <div>
      <label class="label mb-2 text-sm" for="cookie-profile-key">Cookie profile key</label>
      <Select id="cookie-profile-key" items={cookieProfileItems} bind:value={cookieProfileKey} class="text-sm" />
    </div>
  </div>

  <div>
    <label class="label mb-2 text-sm" for="worker-tag">Worker tag</label>
    <input class="input w-full text-sm" id="worker-tag"
       pattern={'[a-z0-9\\-]{2,50}'} minlength={2} maxlength={50} bind:value={workerTag} placeholder="gpu-encode" />
    <p class="mt-1.5 text-xs text-base-content/40">
      Optional. Routes jobs from this config set to workers configured with this tag, overriding the storage target's own tag. Leave blank to use the storage target's default routing.
    </p>
  </div>

  <div>
    <label class="label mb-2 text-sm" for="option-preset">Option preset</label>
    {#if optionPresetsLoading}
      <div class="flex items-center gap-2 rounded-field border-[length:var(--border)] border-base-300 bg-base-200/60 px-3 py-2 text-sm text-base-content/50">
        <span class="loading loading-spinner loading-xs"></span>
        Loading option presets...
      </div>
    {:else}
      <Select id="option-preset" items={optionPresetItems} bind:value={selectedOptionPresetKey} class="text-sm" />
    {/if}
    <p class="mt-1.5 text-xs text-base-content/40">
      Pick a saved option preset to apply its yt-dlp options to this config set. Choose none to use server defaults.
    </p>
    {#if optionPresetsError}
      <div class="alert alert-error mt-3" role="alert">
        {optionPresetsError}
      </div>
    {:else if !optionPresetsLoading && optionPresets.length === 0 && !initial?.ytDlpOptions}
      <div class="alert mt-3">
        No option presets exist yet. Create one from Profile → Option presets if this config set needs custom yt-dlp options.
      </div>
    {/if}
  </div>

  <div>
    <label class="label mb-2 text-sm" for="priority">
      Priority <span class="font-normal text-base-content/50">({priority})</span>
    </label>
    <input id="priority" type="range" min="0" max="100" step="1" bind:value={priority} class="range range-primary w-full" />
  </div>

  <div>
    <label class="label mb-2 text-sm" for="ignore-keywords">Ignore keywords</label>
    <textarea class="textarea w-full font-mono text-sm" id="ignore-keywords" rows={5} bind:value={ignoreKeywordsText} placeholder={'shorts\nregex:\\btrailer\\b'}></textarea>
    <p class="mt-1.5 text-xs text-base-content/40">One per line. Prefix with <code>regex:</code> for regex matching.</p>
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
    <a class="btn btn-sm btn-neutral text-xs" href="/profile/config-sets">
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
      {isUpdate ? 'Save changes' : 'Create config set'}
    </button>
  </div>
</form>
