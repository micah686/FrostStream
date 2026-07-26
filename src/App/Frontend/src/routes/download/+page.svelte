<script lang="ts">
  import { onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    CircleAlert,
    CircleCheck,
    Download,
    List,
    Users,
    Video,
    X
  } from '@lucide/svelte';
  import { listOptionPresets, type OptionPreset } from '$lib/api/optionPresets';
  import { listDownloadConfigSets, type DownloadConfigSet } from '$lib/api/downloadConfigSets';
  import { queuePlaylistDownload } from '$lib/api/playlists';
  import { creatorSourceTypes, queueChannelDownload, type CreatorSourceType } from '$lib/api/creatorSources';
  import RangeSlider from '$lib/components/RangeSlider.svelte';

  type TabKey = 'video' | 'playlist' | 'creator';

  interface SelectItem {
    value: string;
    name: string;
  }

  interface QueuedJob {
    kind: TabKey;
    id: string;
    sourceUrl: string;
    storageKey: string;
    queuedAt: Date;
  }


  const tabs: { key: TabKey; label: string; icon: typeof Video }[] = [
    { key: 'video', label: 'Video', icon: Video },
    { key: 'playlist', label: 'Playlist', icon: List },
    { key: 'creator', label: 'Creator', icon: Users }
  ];

  const kindLabels: Record<TabKey, string> = {
    video: 'Video',
    playlist: 'Playlist',
    creator: 'Creator'
  };

  const sourceTypeOptions = creatorSourceTypes.map((type) => ({ value: type, name: type }));

  let activeTab = $state<TabKey>('video');

  // Video form
  let sourceUrl = $state('');
  let forceDownload = $state(false);
  let tags = $state('');
  let fetchComments = $state(false);

  let optionPresets = $state<OptionPreset[]>([]);
  let optionPresetKey = $state('');
  let audioOnlyOverride = $state<boolean | null>(null);
  let downloadInfoJsonOverride = $state<boolean | null>(null);
  let downloadThumbnailOverride = $state<boolean | null>(null);
  let downloadSubtitlesOverride = $state<boolean | null>(null);

  // Playlist form
  let playlistUrl = $state('');
  let playlistConfigSetKey = $state('');
  let playlistEncode = $state(false);
  let playlistFetchComments = $state(false);

  // Creator form
  let creatorUrl = $state('');
  let creatorPlatform = $state('youtube');
  let creatorSourceType = $state<CreatorSourceType>('Videos');
  let creatorConfigSetKey = $state('');
  let creatorFetchComments = $state(false);
  let creatorForceDownload = $state(false);

  // Shared fields
  let storageKey = $state('default');
  let cookieProfileKey = $state('');
  let priority = $state(0);

  let storageOptions = $state<SelectItem[]>([{ value: 'default', name: 'default' }]);
  let storageLoadFailed = $state(false);
  let cookieOptions = $state<SelectItem[]>([]);
  let configSets = $state<DownloadConfigSet[]>([]);

  let submitting = $state(false);
  let submitError = $state<string | null>(null);
  let queued = $state<QueuedJob[]>([]);

  const configSetOptions = $derived([
    { value: '', name: 'None (use per-request settings)' },
    ...configSets.map((set) => ({ value: set.key, name: set.name }))
  ]);

  onMount(() => {
    void loadStorageTargets();
    void loadCookieProfiles();
    void loadOptionPresets();
    void loadConfigSets();
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
      const response = await fetch('/api/user/cookies');
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

  async function loadConfigSets() {
    try {
      configSets = await listDownloadConfigSets();
    } catch {
      // Config sets are optional; playlist and creator downloads work without them.
    }
  }

  function switchTab(tab: TabKey) {
    activeTab = tab;
    submitError = null;
  }

  function selectedPreset(): OptionPreset | null {
    return optionPresets.find((preset) => preset.key === optionPresetKey) ?? null;
  }

  function optionGroup(options: Record<string, unknown> | null | undefined, name: string): Record<string, unknown> {
    const group = options?.[name];
    return group && typeof group === 'object' && !Array.isArray(group) ? (group as Record<string, unknown>) : {};
  }

  function buildYtDlpOptions(): Record<string, unknown> {
    const base: Record<string, unknown> = structuredClone($state.snapshot(selectedPreset()?.ytDlpOptions ?? {}));

    if (audioOnlyOverride !== null) {
      base.postProcessing = { ...optionGroup(base, 'postProcessing'), extractAudio: audioOnlyOverride };
    }

    if (downloadInfoJsonOverride !== null) {
      base.filesystem = { ...optionGroup(base, 'filesystem'), writeInfoJson: downloadInfoJsonOverride };
    }

    if (downloadThumbnailOverride !== null) {
      base.thumbnail = {
        ...optionGroup(base, 'thumbnail'),
        writeThumbnail: downloadThumbnailOverride,
        noWriteThumbnail: !downloadThumbnailOverride
      };
    }

    if (downloadSubtitlesOverride !== null) {
      base.subtitle = {
        ...optionGroup(base, 'subtitle'),
        writeSubs: downloadSubtitlesOverride,
        noWriteSubs: !downloadSubtitlesOverride,
        ...(downloadSubtitlesOverride ? { subLangs: 'all,-live_chat' } : {})
      };
    }

    return base;
  }

  function overrideStatus(value: boolean | null): string {
    return value === null ? 'Preset' : value ? 'On' : 'Off';
  }

  function parseTags(value: string): string[] {
    return value
      .split(',')
      .map((tag) => tag.trim())
      .filter(Boolean);
  }

  function resolvedStorageKey(): string {
    return storageKey.trim() || 'default';
  }

  function recordQueued(kind: TabKey, id: string, url: string) {
    queued = [{ kind, id, sourceUrl: url, storageKey: resolvedStorageKey(), queuedAt: new Date() }, ...queued];
  }

  async function queueVideoDownload(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    const body = {
      sourceUrl: sourceUrl.trim(),
      storageKey: resolvedStorageKey(),
      forceDownload,
      tags: parseTags(tags),
      cookieProfileKey: cookieProfileKey || null,
      priority,
      ytDlpOptions: buildYtDlpOptions(),
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

      const result = (await response.json()) as {
        jobId?: string;
        playlistId?: string;
        kind?: string;
      };
      // Playlist-container URLs are auto-routed server-side into the playlist pipeline
      // and return a playlistId instead of a jobId.
      if (result.kind === 'playlist' && result.playlistId) {
        recordQueued('playlist', `playlist ${result.playlistId}`, body.sourceUrl);
      } else {
        recordQueued('video', `job ${result.jobId}`, body.sourceUrl);
      }
      sourceUrl = '';
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'The download request failed.';
    } finally {
      submitting = false;
    }
  }

  async function queuePlaylist(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    const url = playlistUrl.trim();
    try {
      const result = await queuePlaylistDownload({
        sourceUrl: url,
        storageKey: resolvedStorageKey(),
        configSetKey: playlistConfigSetKey || null,
        cookieProfileKey: cookieProfileKey || null,
        encodeForPlaylist: playlistEncode,
        priority,
        fetchComments: playlistFetchComments
      });
      recordQueued('playlist', `playlist ${result.playlistId}`, url);
      playlistUrl = '';
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'The playlist request failed.';
    } finally {
      submitting = false;
    }
  }

  async function queueCreator(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    const url = creatorUrl.trim();
    try {
      const result = await queueChannelDownload({
        sourceUrl: url,
        platform: creatorPlatform.trim() || 'youtube',
        sourceType: creatorSourceType,
        storageKey: resolvedStorageKey(),
        configSetKey: creatorConfigSetKey || null,
        cookieProfileKey: cookieProfileKey || null,
        priority,
        fetchComments: creatorFetchComments,
        forceDownload: creatorForceDownload
      });
      recordQueued('creator', `group ${result.correlationId}`, url);
      creatorUrl = '';
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'The channel download request failed.';
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

{#snippet sharedFields()}
  <div class="grid gap-5 sm:grid-cols-2">
    <div>
      <label class="label mb-2 text-sm" for="storage-key">Storage target</label>
      {#if storageLoadFailed}
        <input class="input w-full" id="storage-key" bind:value={storageKey} placeholder="default" />
        <p class="mt-1.5 text-xs text-warning">
          Could not load storage targets; enter a storage key manually.
        </p>
      {:else}
        <Select id="storage-key" items={storageOptions} bind:value={storageKey} />
      {/if}
    </div>

    <div>
      <label class="label mb-2 text-sm" for="cookie-profile">Cookie profile</label>
      <Select id="cookie-profile" items={[{ value: '', name: 'None' }, ...cookieOptions]} bind:value={cookieProfileKey} />
    </div>
  </div>
{/snippet}

{#snippet prioritySlider()}
  <div>
    <label class="label mb-2 text-sm" for="priority">
      Priority <span class="font-normal text-base-content/50">({priority})</span>
    </label>
    <RangeSlider id="priority" min={0} max={100} step={1} bind:value={priority} />
    <p class="mt-1.5 text-xs text-base-content/40">Higher runs first while jobs wait for a slot.</p>
  </div>
{/snippet}

{#snippet submitRow(label: string)}
  {#if submitError}
    <div
      class="flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{submitError}</span>
    </div>
  {/if}

  <div class="flex justify-end border-t border-base-300/70 pt-5">
    <button class="btn btn-sm btn-primary" type="submit" disabled={submitting}>
      {#if submitting}
        <span class="loading loading-spinner loading-xs mr-2"></span>
      {:else}
        <Download class="mr-2 h-4 w-4" />
      {/if}
      {label}
    </button>
  </div>
{/snippet}

<section class="mx-auto max-w-3xl" aria-labelledby="download-title">
  <div class="mb-6">
    <h1 id="download-title" class="text-2xl font-bold tracking-tight text-base-content">Download</h1>
    <p class="mt-1 text-sm text-base-content/50">
      Queue a media download on the server. Jobs run in the background; this page only submits them.
    </p>
  </div>

  <div class="mb-5 flex gap-2" role="tablist" aria-label="Download type">
    {#each tabs as tab (tab.key)}
      {@const Icon = tab.icon}
      <button
        type="button"
        role="tab"
        aria-selected={activeTab === tab.key}
        onclick={() => switchTab(tab.key)}
        class={[
          'inline-flex h-10 items-center gap-2 rounded-full px-5 text-sm font-semibold transition',
          activeTab === tab.key
            ? 'bg-base-content text-base-100'
            : 'bg-base-300/75 text-base-content/80 hover:bg-base-300'
        ]}
      >
        <Icon class="h-4 w-4" />
        {tab.label}
      </button>
    {/each}
  </div>

  {#if activeTab === 'video'}
    <form
      onsubmit={queueVideoDownload}
      class="rounded-2xl border border-base-300/80 bg-base-200/40 p-6 shadow-2xl shadow-black/20 sm:p-8"
    >
      <div class="space-y-5">
        <div>
          <label class="label mb-2 text-sm" for="source-url">Source URL</label>
          <input class="input w-full" id="source-url" type="url" required
             bind:value={sourceUrl} placeholder="https://www.youtube.com/watch?v=..." />
        </div>

        {@render sharedFields()}

        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="tags">Worker Tags</label>
            <input class="input w-full" id="tags" bind:value={tags} placeholder="aws-wkr, local, import, proxmx-wk-003,..." />
            <p class="mt-1.5 text-xs text-base-content/40">Comma separated, optional.</p>
          </div>

          {@render prioritySlider()}
        </div>

        <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-base-300/70 pt-5">
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={forceDownload} /><span>Force download <span class="ml-1 text-xs text-base-content/40">(re-download even if it already exists)</span></span></label>
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={fetchComments} /><span>Fetch comments</span></label>
        </div>

        <div class="rounded-xl border border-base-300/70 bg-base-200/40 p-4">
          <div class="max-w-sm">
            <label class="label mb-2 text-sm" for="option-preset">Option preset</label>
            <Select id="option-preset" items={[
 { value: '', name: 'None' },
                ...optionPresets.map((preset) => ({ value: preset.key, name: preset.name }))
              ]} bind:value={optionPresetKey} />
            <p class="mt-1.5 text-xs text-base-content/40">
              yt-dlp options from the preset are applied to this download. The controls below only override a preset after you change them.
            </p>
          </div>

          <div class="mt-4 flex flex-wrap gap-x-8 gap-y-3 border-t border-base-300/70 pt-4">
            <div class="flex items-center gap-2">
              <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle" checked={audioOnlyOverride ?? false} onchange={(event) => (audioOnlyOverride = event.currentTarget.checked)} /><span>Audio only</span></label>
              <span class="text-xs text-base-content/40">{overrideStatus(audioOnlyOverride)}</span>
              {#if audioOnlyOverride !== null}
                <button type="button" aria-label="Use preset audio setting" title="Use preset" onclick={() => (audioOnlyOverride = null)} class="rounded p-1 text-base-content/50 hover:bg-base-300 hover:text-base-content/80">
                  <X class="h-3.5 w-3.5" />
                </button>
              {/if}
            </div>
            <div class="flex items-center gap-2">
              <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle" checked={downloadInfoJsonOverride ?? false} onchange={(event) => (downloadInfoJsonOverride = event.currentTarget.checked)} /><span>Download info JSON</span></label>
              <span class="text-xs text-base-content/40">{overrideStatus(downloadInfoJsonOverride)}</span>
              {#if downloadInfoJsonOverride !== null}
                <button type="button" aria-label="Use preset info JSON setting" title="Use preset" onclick={() => (downloadInfoJsonOverride = null)} class="rounded p-1 text-base-content/50 hover:bg-base-300 hover:text-base-content/80">
                  <X class="h-3.5 w-3.5" />
                </button>
              {/if}
            </div>
            <div class="flex items-center gap-2">
              <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle" checked={downloadThumbnailOverride ?? false} onchange={(event) => (downloadThumbnailOverride = event.currentTarget.checked)} /><span>Download thumbnail</span></label>
              <span class="text-xs text-base-content/40">{overrideStatus(downloadThumbnailOverride)}</span>
              {#if downloadThumbnailOverride !== null}
                <button type="button" aria-label="Use preset thumbnail setting" title="Use preset" onclick={() => (downloadThumbnailOverride = null)} class="rounded p-1 text-base-content/50 hover:bg-base-300 hover:text-base-content/80">
                  <X class="h-3.5 w-3.5" />
                </button>
              {/if}
            </div>
            <div class="flex items-center gap-2">
              <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle" checked={downloadSubtitlesOverride ?? false} onchange={(event) => (downloadSubtitlesOverride = event.currentTarget.checked)} /><span>Download subtitles (all)</span></label>
              <span class="text-xs text-base-content/40">{overrideStatus(downloadSubtitlesOverride)}</span>
              {#if downloadSubtitlesOverride !== null}
                <button type="button" aria-label="Use preset subtitles setting" title="Use preset" onclick={() => (downloadSubtitlesOverride = null)} class="rounded p-1 text-base-content/50 hover:bg-base-300 hover:text-base-content/80">
                  <X class="h-3.5 w-3.5" />
                </button>
              {/if}
            </div>
          </div>
        </div>

        {@render submitRow('Queue download')}
      </div>
    </form>
  {:else if activeTab === 'playlist'}
    <form
      onsubmit={queuePlaylist}
      class="rounded-2xl border border-base-300/80 bg-base-200/40 p-6 shadow-2xl shadow-black/20 sm:p-8"
    >
      <div class="space-y-5">
        <div>
          <label class="label mb-2 text-sm" for="playlist-url">Playlist URL</label>
          <input class="input w-full" id="playlist-url" type="url" required
             bind:value={playlistUrl} placeholder="https://www.youtube.com/playlist?list=..." />
          <p class="mt-1.5 text-xs text-base-content/40">
            Every entry in the playlist is queued as its own download job, keeping the provider's order.
          </p>
        </div>

        {@render sharedFields()}

        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="playlist-config-set">Config set</label>
            <Select id="playlist-config-set" items={configSetOptions} bind:value={playlistConfigSetKey} />
            <p class="mt-1.5 text-xs text-base-content/40">
              Applies its saved yt-dlp options and ignore keywords to every entry.
            </p>
          </div>

          {@render prioritySlider()}
        </div>

        <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-base-300/70 pt-5">
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={playlistEncode} /><span>Encode for playlist <span class="ml-1 text-xs text-base-content/40">(re-encode for gapless playback)</span></span></label>
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={playlistFetchComments} /><span>Fetch comments</span></label>
        </div>

        {@render submitRow('Queue playlist')}
      </div>
    </form>
  {:else}
    <form
      onsubmit={queueCreator}
      class="rounded-2xl border border-base-300/80 bg-base-200/40 p-6 shadow-2xl shadow-black/20 sm:p-8"
    >
      <div class="space-y-5">
        <div>
          <label class="label mb-2 text-sm" for="creator-url">Channel URL</label>
          <input class="input w-full" id="creator-url" type="url" required
             bind:value={creatorUrl} placeholder="https://www.youtube.com/@creator/videos" />
          <p class="mt-1.5 text-xs text-base-content/40">
            Downloads the channel's full backlog and registers it as a tracked creator, so new uploads are
            discovered automatically. Manage tracked creators on the
            <a href="/creators" class="font-semibold text-primary hover:underline">Creators</a> page.
          </p>
        </div>

        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="creator-platform">Platform</label>
            <input class="input w-full" id="creator-platform" required  bind:value={creatorPlatform} placeholder="youtube" />
          </div>
          <div>
            <label class="label mb-2 text-sm" for="creator-source-type">Content type</label>
            <Select id="creator-source-type" items={sourceTypeOptions} bind:value={creatorSourceType} />
          </div>
        </div>

        {@render sharedFields()}

        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="creator-config-set">Config set</label>
            <Select id="creator-config-set" items={configSetOptions} bind:value={creatorConfigSetKey} />
            <p class="mt-1.5 text-xs text-base-content/40">
              Applies its saved yt-dlp options and ignore keywords to every discovered video.
            </p>
          </div>

          {@render prioritySlider()}
        </div>

        <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-base-300/70 pt-5">
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={creatorForceDownload} /><span>Force download <span class="ml-1 text-xs text-base-content/40">(re-download videos already in the library)</span></span></label>
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={creatorFetchComments} /><span>Fetch comments</span></label>
        </div>

        {@render submitRow('Queue channel download')}
      </div>
    </form>
  {/if}

  {#if queued.length > 0}
    <div class="mt-6 rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <h2 class="text-sm font-bold uppercase tracking-[0.08em] text-base-content/50">Queued this session</h2>
      <ul class="mt-3 space-y-2">
        {#each queued as job (job.id)}
          <li class="flex items-center gap-3 rounded-xl bg-base-200/40 px-4 py-3">
            <CircleCheck class="h-5 w-5 shrink-0 text-success" />
            <div class="min-w-0 flex-1">
              <p class="truncate text-sm text-base-content/90">{job.sourceUrl}</p>
              <p class="mt-0.5 truncate font-mono text-xs text-base-content/40">{job.id}</p>
            </div>
            <span class="badge badge-sm badge-primary rounded-full shrink-0 text-xs">
              {kindLabels[job.kind]}
            </span>
            <span class="badge badge-sm badge-ghost rounded-full shrink-0 text-xs">
              {job.storageKey}
            </span>
            <span class="shrink-0 text-xs text-base-content/40">
              {job.queuedAt.toLocaleTimeString()}
            </span>
          </li>
        {/each}
      </ul>
    </div>
  {/if}
</section>
