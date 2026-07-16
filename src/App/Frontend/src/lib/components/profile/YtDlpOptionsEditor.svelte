<script lang="ts">
  import { untrack } from 'svelte';
  import { Checkbox, Input, Label, Select, Textarea, Toggle } from 'flowbite-svelte';
  import { ChevronDownOutline } from 'flowbite-svelte-icons';
  import TriStateSelect from './TriStateSelect.svelte';
  import {
    applyStateToOptions,
    audioFormatItems,
    audioQualityItems,
    containerItems,
    resolutionItems,
    sponsorBlockCategories,
    sponsorBlockMarkOnlyCategories,
    stateFromOptions,
    subtitleFormatItems
  } from './ytDlpPresetOptions';

  interface Props {
    value?: Record<string, unknown>;
  }

  let { value = $bindable({}) }: Props = $props();

  const fieldClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';

  // Options the GUI does not manage are carried through from the loaded preset.
  const base = untrack(() => clonePlainOptions(value));
  let opts = $state(untrack(() => stateFromOptions(base)));

  $effect(() => {
    value = applyStateToOptions(opts, base);
  });

  const markCategories = [...sponsorBlockCategories, ...sponsorBlockMarkOnlyCategories];

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

  function toggleCategory(list: string[], category: string, checked: boolean): string[] {
    const without = list.filter((entry) => entry !== category);
    return checked ? [...without, category] : without;
  }
</script>

<div class="space-y-4">
  <p class="text-xs text-slate-500">
    Everything is optional — leave a field on “Default” to use the server’s normal behavior for that
    setting. Only the values you change are stored in the preset.
  </p>

  <details open class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-slate-200">Video quality & format</h3>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <div>
        <Label for="opt-resolution" class="mb-2 text-sm font-medium text-slate-300">Maximum resolution</Label>
        <Select id="opt-resolution" items={resolutionItems} bind:value={opts.resolution} class={fieldClass} />
      </div>
      <div>
        <Label for="opt-container" class="mb-2 text-sm font-medium text-slate-300">Container</Label>
        <Select id="opt-container" items={containerItems} bind:value={opts.container} class={fieldClass} />
      </div>
      {#if opts.resolution === 'custom'}
        <div class="sm:col-span-2">
          <Label for="opt-custom-format" class="mb-2 text-sm font-medium text-slate-300">Custom format string</Label>
          <Input
            id="opt-custom-format"
            bind:value={opts.customFormat}
            placeholder="bestvideo[height<=1080]+bestaudio/best"
            class="font-mono! {fieldClass}"
          />
          <p class="mt-1.5 text-xs text-slate-600">A raw yt-dlp format selector, for advanced use.</p>
        </div>
      {/if}
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-slate-200">Audio</h3>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 space-y-4">
      <Toggle bind:checked={opts.audioOnly} class="text-sm text-slate-300">
        Audio only <span class="ml-1 text-xs text-slate-600">(skip the video, keep just the sound)</span>
      </Toggle>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <Label for="opt-audio-format" class="mb-2 text-sm font-medium text-slate-300">Audio format</Label>
          <Select
            id="opt-audio-format"
            items={audioFormatItems}
            bind:value={opts.audioFormat}
            disabled={!opts.audioOnly}
            class="{fieldClass} disabled:opacity-60"
          />
        </div>
        <div>
          <Label for="opt-audio-quality" class="mb-2 text-sm font-medium text-slate-300">Audio quality</Label>
          <Select
            id="opt-audio-quality"
            items={audioQualityItems}
            bind:value={opts.audioQuality}
            disabled={!opts.audioOnly}
            class="{fieldClass} disabled:opacity-60"
          />
        </div>
      </div>
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-slate-200">Subtitles</h3>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <TriStateSelect id="opt-write-subs" label="Download subtitles" bind:value={opts.writeSubs} />
      <TriStateSelect id="opt-write-auto-subs" label="Auto-generated subtitles" bind:value={opts.writeAutoSubs} />
      <div>
        <Label for="opt-sub-langs" class="mb-2 text-sm font-medium text-slate-300">Languages</Label>
        <Input id="opt-sub-langs" bind:value={opts.subLangs} placeholder="en.*,ja or all" class={fieldClass} />
        <p class="mt-1.5 text-xs text-slate-600">Comma separated language codes.</p>
      </div>
      <div>
        <Label for="opt-sub-format" class="mb-2 text-sm font-medium text-slate-300">Subtitle format</Label>
        <Select id="opt-sub-format" items={subtitleFormatItems} bind:value={opts.subFormat} class={fieldClass} />
      </div>
      <TriStateSelect id="opt-embed-subs" label="Embed subtitles in the video" bind:value={opts.embedSubs} />
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-slate-200">Thumbnails & metadata</h3>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <TriStateSelect id="opt-write-thumbnail" label="Save thumbnail file" bind:value={opts.writeThumbnail} />
      <TriStateSelect id="opt-embed-thumbnail" label="Embed thumbnail" bind:value={opts.embedThumbnail} />
      <TriStateSelect id="opt-embed-metadata" label="Embed metadata" bind:value={opts.embedMetadata} />
      <TriStateSelect id="opt-embed-chapters" label="Embed chapters" bind:value={opts.embedChapters} />
      <TriStateSelect id="opt-write-description" label="Save description file" bind:value={opts.writeDescription} />
      <TriStateSelect id="opt-write-info-json" label="Save info JSON file" bind:value={opts.writeInfoJson} />
      <TriStateSelect id="opt-embed-info-json" label="Embed info JSON" bind:value={opts.embedInfoJson} />
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <div>
        <h3 class="text-sm font-semibold text-slate-200">SponsorBlock</h3>
        <p class="mt-1 text-xs text-slate-600">Marks or removes sponsored segments using community data.</p>
      </div>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 space-y-4">
      <Checkbox bind:checked={opts.sponsorBlockDisabled} class="text-sm text-slate-300">
        Disable SponsorBlock entirely
      </Checkbox>
      {#if !opts.sponsorBlockDisabled}
        <div>
          <p class="mb-2 text-sm font-medium text-slate-300">Mark segments as chapters</p>
          <div class="grid gap-x-6 gap-y-2 sm:grid-cols-2 lg:grid-cols-3">
            {#each markCategories as category (category.value)}
              <Checkbox
                checked={opts.sponsorBlockMark.includes(category.value)}
                onchange={(event) =>
                  (opts.sponsorBlockMark = toggleCategory(
                    opts.sponsorBlockMark,
                    category.value,
                    event.currentTarget.checked
                  ))}
                class="text-sm text-slate-300"
              >
                {category.name}
              </Checkbox>
            {/each}
          </div>
        </div>
        <div>
          <p class="mb-2 text-sm font-medium text-slate-300">Cut segments out of the video</p>
          <div class="grid gap-x-6 gap-y-2 sm:grid-cols-2 lg:grid-cols-3">
            {#each sponsorBlockCategories as category (category.value)}
              <Checkbox
                checked={opts.sponsorBlockRemove.includes(category.value)}
                onchange={(event) =>
                  (opts.sponsorBlockRemove = toggleCategory(
                    opts.sponsorBlockRemove,
                    category.value,
                    event.currentTarget.checked
                  ))}
                class="text-sm text-slate-300"
              >
                {category.name}
              </Checkbox>
            {/each}
          </div>
        </div>
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <Label for="opt-sb-chapter-title" class="mb-2 text-sm font-medium text-slate-300">Chapter title template</Label>
            <Input
              id="opt-sb-chapter-title"
              bind:value={opts.sponsorBlockChapterTitle}
              placeholder="[SponsorBlock]: %(category_names)l"
              class={fieldClass}
            />
          </div>
          <div>
            <Label for="opt-sb-api" class="mb-2 text-sm font-medium text-slate-300">SponsorBlock API URL</Label>
            <Input
              id="opt-sb-api"
              type="url"
              bind:value={opts.sponsorBlockApi}
              placeholder="https://sponsor.ajay.app"
              class={fieldClass}
            />
          </div>
        </div>
      {/if}
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-slate-200">Download behavior & limits</h3>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <div>
        <Label for="opt-limit-rate" class="mb-2 text-sm font-medium text-slate-300">Speed limit</Label>
        <Input
          id="opt-limit-rate"
          bind:value={opts.limitRate}
          pattern={'\\d+(\\.\\d+)?[KMG]?'}
          placeholder="e.g. 4.2M"
          class={fieldClass}
        />
        <p class="mt-1.5 text-xs text-slate-600">Maximum download rate, e.g. 50K or 4.2M.</p>
      </div>
      <div>
        <Label for="opt-concurrent-fragments" class="mb-2 text-sm font-medium text-slate-300">Concurrent fragments</Label>
        <Input
          id="opt-concurrent-fragments"
          type="number"
          min={1}
          max={16}
          bind:value={opts.concurrentFragments}
          placeholder="Default"
          class={fieldClass}
        />
      </div>
      <div>
        <Label for="opt-retries" class="mb-2 text-sm font-medium text-slate-300">Retries</Label>
        <Input id="opt-retries" bind:value={opts.retries} placeholder={'e.g. 10 or "infinite"'} class={fieldClass} />
      </div>
      <div>
        <Label for="opt-playlist-items" class="mb-2 text-sm font-medium text-slate-300">Playlist items</Label>
        <Input id="opt-playlist-items" bind:value={opts.playlistItems} placeholder="e.g. 1:100" class={fieldClass} />
        <p class="mt-1.5 text-xs text-slate-600">Which entries to take when the link is a playlist.</p>
      </div>
      <div>
        <Label for="opt-max-filesize" class="mb-2 text-sm font-medium text-slate-300">Max file size</Label>
        <Input id="opt-max-filesize" bind:value={opts.maxFilesize} placeholder="e.g. 500M" class={fieldClass} />
      </div>
      <div>
        <Label for="opt-min-filesize" class="mb-2 text-sm font-medium text-slate-300">Min file size</Label>
        <Input id="opt-min-filesize" bind:value={opts.minFilesize} placeholder="e.g. 1M" class={fieldClass} />
      </div>
      <div>
        <Label for="opt-date-after" class="mb-2 text-sm font-medium text-slate-300">Uploaded on or after</Label>
        <Input id="opt-date-after" type="date" bind:value={opts.dateAfter} class={fieldClass} />
      </div>
      <div>
        <Label for="opt-date-before" class="mb-2 text-sm font-medium text-slate-300">Uploaded on or before</Label>
        <Input id="opt-date-before" type="date" bind:value={opts.dateBefore} class={fieldClass} />
      </div>
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <div>
        <h3 class="text-sm font-semibold text-slate-200">Network & authentication</h3>
        <p class="mt-1 text-xs text-slate-600">For sites that need a login or a proxy. Prefer cookie profiles for site logins when possible.</p>
      </div>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <div class="sm:col-span-2">
        <Label for="opt-proxy" class="mb-2 text-sm font-medium text-slate-300">Proxy URL</Label>
        <Input
          id="opt-proxy"
          bind:value={opts.proxy}
          placeholder="socks5://127.0.0.1:1080"
          class={fieldClass}
        />
        <p class="mt-1.5 text-xs text-slate-600">Routes the server’s download traffic through this proxy.</p>
      </div>
      <div>
        <Label for="opt-username" class="mb-2 text-sm font-medium text-slate-300">Username</Label>
        <Input id="opt-username" autocomplete="off" bind:value={opts.username} class={fieldClass} />
      </div>
      <div>
        <Label for="opt-password" class="mb-2 text-sm font-medium text-slate-300">Password</Label>
        <Input id="opt-password" type="password" autocomplete="new-password" bind:value={opts.password} class={fieldClass} />
      </div>
      <div>
        <Label for="opt-two-factor" class="mb-2 text-sm font-medium text-slate-300">Two-factor code</Label>
        <Input id="opt-two-factor" autocomplete="off" bind:value={opts.twoFactor} class={fieldClass} />
      </div>
      <div>
        <Label for="opt-video-password" class="mb-2 text-sm font-medium text-slate-300">Video password</Label>
        <Input id="opt-video-password" type="password" autocomplete="new-password" bind:value={opts.videoPassword} class={fieldClass} />
      </div>
      <p class="text-xs text-amber-500/80 sm:col-span-2">
        Credentials are stored as plain text in the preset and sent to the site during downloads.
      </p>
    </div>
  </details>

  <details class="group rounded-xl border border-slate-800/70 bg-slate-950/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <div>
        <h3 class="text-sm font-semibold text-slate-200">Workarounds</h3>
        <p class="mt-1 text-xs text-slate-600">Only needed for sites that misbehave with the default settings.</p>
      </div>
      <ChevronDownOutline class="h-4 w-4 shrink-0 text-slate-500 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 space-y-4">
      <div class="flex flex-wrap gap-x-8 gap-y-3">
        <Toggle bind:checked={opts.noCheckCertificates} class="text-sm text-slate-300">
          Skip certificate checks
        </Toggle>
        <Toggle bind:checked={opts.legacyServerConnect} class="text-sm text-slate-300">
          Allow legacy server connections
        </Toggle>
      </div>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <Label for="opt-sleep-requests" class="mb-2 text-sm font-medium text-slate-300">Sleep between requests (s)</Label>
          <Input id="opt-sleep-requests" type="number" min={0} step="any" bind:value={opts.sleepRequests} placeholder="Default" class={fieldClass} />
        </div>
        <div>
          <Label for="opt-sleep-subtitles" class="mb-2 text-sm font-medium text-slate-300">Sleep between subtitles (s)</Label>
          <Input id="opt-sleep-subtitles" type="number" min={0} step="any" bind:value={opts.sleepSubtitles} placeholder="Default" class={fieldClass} />
        </div>
        <div>
          <Label for="opt-sleep-interval" class="mb-2 text-sm font-medium text-slate-300">Sleep before download (s)</Label>
          <Input id="opt-sleep-interval" type="number" min={0} step="any" bind:value={opts.sleepInterval} placeholder="Default" class={fieldClass} />
        </div>
        <div>
          <Label for="opt-max-sleep-interval" class="mb-2 text-sm font-medium text-slate-300">Max sleep before download (s)</Label>
          <Input id="opt-max-sleep-interval" type="number" min={0} step="any" bind:value={opts.maxSleepInterval} placeholder="Default" class={fieldClass} />
        </div>
      </div>
      <div>
        <Label for="opt-add-headers" class="mb-2 text-sm font-medium text-slate-300">Extra HTTP headers</Label>
        <Textarea
          id="opt-add-headers"
          rows={3}
          bind:value={opts.addHeaders}
          placeholder={'Referer: https://example.com\nX-Custom: value'}
          class="font-mono! {fieldClass}"
        />
        <p class="mt-1.5 text-xs text-slate-600">One <code>Header: value</code> per line.</p>
      </div>
    </div>
  </details>
</div>
