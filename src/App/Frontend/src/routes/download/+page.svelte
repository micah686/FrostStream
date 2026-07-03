<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Checkbox, Input, Label, Select, Spinner, Toggle } from 'flowbite-svelte';
  import {
    CheckCircleOutline,
    DownloadOutline,
    ExclamationCircleOutline
  } from 'flowbite-svelte-icons';
  import { listOptionPresets, type OptionPreset } from '$lib/api/optionPresets';

  interface SelectItem {
    value: string;
    name: string;
  }

  interface QueuedJob {
    jobId: string;
    sourceUrl: string;
    storageKey: string;
    queuedAt: Date;
  }

  let sourceUrl = $state('');
  let storageKey = $state('default');
  let forceDownload = $state(false);
  let tags = $state('');
  let cookieProfileKey = $state('');
  let priority = $state(0);
  let fetchComments = $state(false);

  let optionPresets = $state<OptionPreset[]>([]);
  let optionPresetKey = $state('');
  let audioOnly = $state(false);
  let downloadInfoJson = $state(false);
  let downloadThumbnail = $state(true);
  let downloadSubtitles = $state(false);

  let sponsorBlockEnabled = $state(false);
  let sbMarkCategories = $state('');
  let sbRemoveCategories = $state('');
  let sbChapterTitleTemplate = $state('');
  let sbApiUrl = $state('');

  let storageOptions = $state<SelectItem[]>([{ value: 'default', name: 'default' }]);
  let storageLoadFailed = $state(false);
  let cookieOptions = $state<SelectItem[]>([]);

  let submitting = $state(false);
  let submitError = $state<string | null>(null);
  let queued = $state<QueuedJob[]>([]);

  onMount(() => {
    void loadStorageTargets();
    void loadCookieProfiles();
    void loadOptionPresets();
  });

  async function loadStorageTargets() {
    try {
      const response = await fetch('/api/storage/list');
      if (!response.ok) {
        throw new Error(`Storage list returned ${response.status}.`);
      }
      const items = (await response.json()) as { key: string; description?: string | null }[];
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
      const response = await fetch('/api/cookies');
      if (!response.ok) {
        return;
      }
      const items = (await response.json()) as {
        profileKey: string;
        displayName?: string | null;
        site?: string | null;
      }[];
      cookieOptions = items.map((item) => ({
        value: item.profileKey,
        name: item.displayName || item.site ? `${item.profileKey} (${item.displayName ?? item.site})` : item.profileKey
      }));
    } catch {
      // Cookie profiles are optional; downloads work without them.
    }
  }

  async function loadOptionPresets() {
    try {
      optionPresets = await listOptionPresets();
    } catch {
      // Option presets are optional; downloads work without them.
    }
  }

  function selectedPreset(): OptionPreset | null {
    return optionPresets.find((preset) => preset.key === optionPresetKey) ?? null;
  }

  function optionGroup(options: Record<string, unknown> | null | undefined, name: string): Record<string, unknown> {
    const group = options?.[name];
    return group && typeof group === 'object' && !Array.isArray(group) ? (group as Record<string, unknown>) : {};
  }

  // Reflect the newly selected preset's values in the toggles; the toggles then override
  // the matching preset values when the download is submitted.
  function syncTogglesFromPreset() {
    const options = selectedPreset()?.ytDlpOptions ?? null;
    audioOnly = optionGroup(options, 'postProcessing').extractAudio === true;
    downloadInfoJson = optionGroup(options, 'filesystem').writeInfoJson === true;
    downloadThumbnail = optionGroup(options, 'thumbnail').noWriteThumbnail !== true;
    downloadSubtitles = optionGroup(options, 'subtitle').writeSubs === true;
  }

  function buildYtDlpOptions(): Record<string, unknown> {
    const base: Record<string, unknown> = structuredClone($state.snapshot(selectedPreset()?.ytDlpOptions ?? {}));

    base.postProcessing = { ...optionGroup(base, 'postProcessing'), extractAudio: audioOnly };
    base.filesystem = { ...optionGroup(base, 'filesystem'), writeInfoJson: downloadInfoJson };
    base.thumbnail = {
      ...optionGroup(base, 'thumbnail'),
      writeThumbnail: downloadThumbnail,
      noWriteThumbnail: !downloadThumbnail
    };
    base.subtitle = {
      ...optionGroup(base, 'subtitle'),
      writeSubs: downloadSubtitles,
      ...(downloadSubtitles ? { subLangs: 'all' } : {})
    };

    return base;
  }

  function parseTags(value: string): string[] {
    return value
      .split(',')
      .map((tag) => tag.trim())
      .filter(Boolean);
  }

  async function queueDownload(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    const body = {
      sourceUrl: sourceUrl.trim(),
      storageKey: storageKey.trim() || 'default',
      forceDownload,
      tags: parseTags(tags),
      cookieProfileKey: cookieProfileKey || null,
      priority,
      ytDlpOptions: buildYtDlpOptions(),
      sponsorBlock: sponsorBlockEnabled
        ? {
            markCategories: sbMarkCategories.trim() || null,
            removeCategories: sbRemoveCategories.trim() || null,
            chapterTitleTemplate: sbChapterTitleTemplate.trim() || null,
            apiUrl: sbApiUrl.trim() || null,
            disable: false
          }
        : {
            markCategories: null,
            removeCategories: null,
            chapterTitleTemplate: null,
            apiUrl: null,
            disable: true
          },
      fetchComments
    };

    try {
      const response = await fetch('/api/downloads/video', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body)
      });

      if (!response.ok) {
        submitError = await describeError(response);
        return;
      }

      const result = (await response.json()) as { jobId: string };
      queued = [
        { jobId: result.jobId, sourceUrl: body.sourceUrl, storageKey: body.storageKey, queuedAt: new Date() },
        ...queued
      ];
      sourceUrl = '';
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'The download request failed.';
    } finally {
      submitting = false;
    }
  }

  async function describeError(response: Response): Promise<string> {
    try {
      const problem = (await response.json()) as { title?: string; detail?: string; errors?: Record<string, string[]> };
      const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : '';
      const text = [problem.title, problem.detail, validation].filter(Boolean).join(' — ');
      if (text) {
        return `${response.status}: ${text}`;
      }
    } catch {
      // Non-JSON error body; fall through to the generic message.
    }
    return `Request failed with status ${response.status}.`;
  }
</script>

<svelte:head>
  <title>Download · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-3xl" aria-labelledby="download-title">
  <div class="mb-6">
    <h1 id="download-title" class="text-2xl font-bold tracking-tight text-white">Download</h1>
    <p class="mt-1 text-sm text-slate-500">
      Queue a media download on the server. Jobs run in the background; this page only submits them.
    </p>
  </div>

  <form
    onsubmit={queueDownload}
    class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-6 shadow-2xl shadow-black/20 sm:p-8"
  >
    <div class="space-y-5">
      <div>
        <Label for="source-url" class="mb-2 text-sm font-medium text-slate-300">Source URL</Label>
        <Input
          id="source-url"
          type="url"
          required
          bind:value={sourceUrl}
          placeholder="https://www.youtube.com/watch?v=..."
          class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
        />
      </div>

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="storage-key" class="mb-2 text-sm font-medium text-slate-300">Storage target</Label>
          {#if storageLoadFailed}
            <Input
              id="storage-key"
              bind:value={storageKey}
              placeholder="default"
              class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
            />
            <p class="mt-1.5 text-xs text-amber-500/80">
              Could not load storage targets; enter a storage key manually.
            </p>
          {:else}
            <Select
              id="storage-key"
              items={storageOptions}
              bind:value={storageKey}
              class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
            />
          {/if}
        </div>

        <div>
          <Label for="cookie-profile" class="mb-2 text-sm font-medium text-slate-300">Cookie profile</Label>
          <Select
            id="cookie-profile"
            items={[{ value: '', name: 'None' }, ...cookieOptions]}
            bind:value={cookieProfileKey}
            class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
          />
        </div>
      </div>

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="tags" class="mb-2 text-sm font-medium text-slate-300">Tags</Label>
          <Input
            id="tags"
            bind:value={tags}
            placeholder="music, live, archive"
            class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
          />
          <p class="mt-1.5 text-xs text-slate-600">Comma separated, optional.</p>
        </div>

        <div>
          <Label for="priority" class="mb-2 text-sm font-medium text-slate-300">
            Priority <span class="font-normal text-slate-500">({priority})</span>
          </Label>
          <input
            id="priority"
            type="range"
            min="0"
            max="100"
            step="1"
            bind:value={priority}
            class="h-2 w-full cursor-pointer appearance-none rounded-full bg-slate-800 accent-blue-500"
          />
          <p class="mt-1.5 text-xs text-slate-600">Higher runs first while jobs wait for a slot.</p>
        </div>
      </div>

      <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-slate-800/70 pt-5">
        <Checkbox bind:checked={forceDownload} class="text-sm text-slate-300">
          Force download <span class="ml-1 text-xs text-slate-600">(re-download even if it already exists)</span>
        </Checkbox>
        <Checkbox bind:checked={fetchComments} class="text-sm text-slate-300">
          Fetch comments
        </Checkbox>
      </div>

      <div class="rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
        <div class="max-w-sm">
          <Label for="option-preset" class="mb-2 text-sm font-medium text-slate-300">Option preset</Label>
          <Select
            id="option-preset"
            items={[
              { value: '', name: 'None' },
              ...optionPresets.map((preset) => ({ value: preset.key, name: preset.name }))
            ]}
            bind:value={optionPresetKey}
            onchange={syncTogglesFromPreset}
            class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
          />
          <p class="mt-1.5 text-xs text-slate-600">
            yt-dlp options from the preset are applied to this download; the toggles below override the matching values.
          </p>
        </div>

        <div class="mt-4 flex flex-wrap gap-x-8 gap-y-3 border-t border-slate-800/70 pt-4">
          <Toggle bind:checked={audioOnly} class="text-sm text-slate-300">
            Audio only
          </Toggle>
          <Toggle bind:checked={downloadInfoJson} class="text-sm text-slate-300">
            Download info JSON
          </Toggle>
          <Toggle bind:checked={downloadThumbnail} class="text-sm text-slate-300">
            Download thumbnail
          </Toggle>
          <Toggle bind:checked={downloadSubtitles} class="text-sm text-slate-300">
            Download subtitles (all)
          </Toggle>
        </div>
      </div>

      <div class="rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
        <Toggle bind:checked={sponsorBlockEnabled} class="text-sm font-medium text-slate-300">
          SponsorBlock
        </Toggle>
        {#if sponsorBlockEnabled}
          <div class="mt-4 grid gap-4 sm:grid-cols-2">
            <div>
              <Label for="sb-mark" class="mb-2 text-xs font-medium text-slate-400">Mark categories</Label>
              <Input
                id="sb-mark"
                bind:value={sbMarkCategories}
                placeholder="all,-preview"
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600!"
              />
            </div>
            <div>
              <Label for="sb-remove" class="mb-2 text-xs font-medium text-slate-400">Remove categories</Label>
              <Input
                id="sb-remove"
                bind:value={sbRemoveCategories}
                placeholder="sponsor,selfpromo"
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600!"
              />
            </div>
            <div>
              <Label for="sb-template" class="mb-2 text-xs font-medium text-slate-400">Chapter title template</Label>
              <Input
                id="sb-template"
                bind:value={sbChapterTitleTemplate}
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200!"
              />
            </div>
            <div>
              <Label for="sb-api" class="mb-2 text-xs font-medium text-slate-400">API URL</Label>
              <Input
                id="sb-api"
                type="url"
                bind:value={sbApiUrl}
                placeholder="https://sponsor.ajay.app"
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600!"
              />
            </div>
          </div>
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

      <div class="flex justify-end border-t border-slate-800/70 pt-5">
        <Button
          type="submit"
          color="blue"
          disabled={submitting}
          class="border-0! bg-blue-500! px-6! py-2.5! font-semibold! shadow-lg shadow-blue-950/30 hover:bg-blue-400! disabled:opacity-60"
        >
          {#if submitting}
            <Spinner size="4" class="mr-2" />
          {:else}
            <DownloadOutline class="mr-2 h-4 w-4" />
          {/if}
          Queue download
        </Button>
      </div>
    </div>
  </form>

  {#if queued.length > 0}
    <div class="mt-6 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <h2 class="text-sm font-bold uppercase tracking-[0.08em] text-slate-500">Queued this session</h2>
      <ul class="mt-3 space-y-2">
        {#each queued as job (job.jobId)}
          <li class="flex items-center gap-3 rounded-xl bg-slate-950/40 px-4 py-3">
            <CheckCircleOutline class="h-5 w-5 shrink-0 text-emerald-400" />
            <div class="min-w-0 flex-1">
              <p class="truncate text-sm text-slate-200">{job.sourceUrl}</p>
              <p class="mt-0.5 truncate font-mono text-xs text-slate-600">job {job.jobId}</p>
            </div>
            <Badge rounded color="gray" class="shrink-0 bg-slate-800! px-2.5! py-0.5! text-xs! text-slate-400!">
              {job.storageKey}
            </Badge>
            <span class="shrink-0 text-xs text-slate-600">
              {job.queuedAt.toLocaleTimeString()}
            </span>
          </li>
        {/each}
      </ul>
    </div>
  {/if}
</section>
