<script lang="ts">
  import { untrack } from 'svelte';
  import { Select } from '$lib/components/ui';
  import { ChevronDown } from '@lucide/svelte';
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
  <p class="text-xs text-base-content/50">
    Everything is optional — leave a field on “Default” to use the server’s normal behavior for that
    setting. Only the values you change are stored in the preset.
  </p>

  <details open class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-base-content/90">Video quality & format</h3>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <div>
        <label class="label mb-2 text-sm" for="opt-resolution">Maximum resolution</label>
        <Select id="opt-resolution" items={resolutionItems} bind:value={opts.resolution} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-container">Container</label>
        <Select id="opt-container" items={containerItems} bind:value={opts.container} />
      </div>
      {#if opts.resolution === 'custom'}
        <div class="sm:col-span-2">
          <label class="label mb-2 text-sm" for="opt-custom-format">Custom format string</label>
          <input class="input w-full font-mono" id="opt-custom-format" bind:value={opts.customFormat} placeholder="bestvideo[height<=1080]+bestaudio/best" />
          <p class="mt-1.5 text-xs text-base-content/40">A raw yt-dlp format selector, for advanced use.</p>
        </div>
      {/if}
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-base-content/90">Audio</h3>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 space-y-4">
      <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle toggle-primary" bind:checked={opts.audioOnly} /><span>Audio only <span class="ml-1 text-xs text-base-content/40">(skip the video, keep just the sound)</span></span></label>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="opt-audio-format">Audio format</label>
          <Select id="opt-audio-format" items={audioFormatItems} bind:value={opts.audioFormat} disabled={!opts.audioOnly} />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="opt-audio-quality">Audio quality</label>
          <Select id="opt-audio-quality" items={audioQualityItems} bind:value={opts.audioQuality} disabled={!opts.audioOnly} />
        </div>
      </div>
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-base-content/90">Subtitles</h3>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <TriStateSelect id="opt-write-subs" label="Download subtitles" bind:value={opts.writeSubs} />
      <TriStateSelect id="opt-write-auto-subs" label="Auto-generated subtitles" bind:value={opts.writeAutoSubs} />
      <div>
        <label class="label mb-2 text-sm" for="opt-sub-langs">Languages</label>
        <input class="input w-full" id="opt-sub-langs" bind:value={opts.subLangs} placeholder="en.*,ja or all" />
        <p class="mt-1.5 text-xs text-base-content/40">Comma separated language codes.</p>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-sub-format">Subtitle format</label>
        <Select id="opt-sub-format" items={subtitleFormatItems} bind:value={opts.subFormat} />
      </div>
      <TriStateSelect id="opt-embed-subs" label="Embed subtitles in the video" bind:value={opts.embedSubs} />
      <div class="sm:col-span-2">
        <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle toggle-primary" bind:checked={opts.includeLiveChat} /><span>Include live chat
          <span class="ml-1 text-xs text-base-content/40">
            (keep the live-chat replay; off by default, so it is dropped even when Languages is “all”)
          </span></span></label>
      </div>
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-base-content/90">Thumbnails & metadata</h3>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
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

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-base-content/90">Comments & live streams</h3>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <TriStateSelect id="opt-fetch-comments" label="Fetch comments" bind:value={opts.fetchComments} />
      <TriStateSelect id="opt-live-from-start" label="Record live streams from the start" bind:value={opts.liveFromStart} />
      <div>
        <label class="label mb-2 text-sm" for="opt-wait-for-video">Wait for scheduled premieres</label>
        <input class="input w-full" id="opt-wait-for-video" bind:value={opts.waitForVideo} placeholder="e.g. 60 (poll every 60s until it airs)" />
        <p class="mt-1.5 text-xs text-base-content/40">Poll interval in seconds; blank means don't wait for an upcoming video.</p>
      </div>
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <div>
        <h3 class="text-sm font-semibold text-base-content/90">SponsorBlock</h3>
        <p class="mt-1 text-xs text-base-content/40">Marks or removes sponsored segments using community data.</p>
      </div>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 space-y-4">
      <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={opts.sponsorBlockDisabled} /><span>Disable SponsorBlock entirely</span></label>
      {#if !opts.sponsorBlockDisabled}
        <div>
          <p class="mb-2 text-sm font-medium text-base-content/80">Mark segments as chapters</p>
          <div class="grid gap-x-6 gap-y-2 sm:grid-cols-2 lg:grid-cols-3">
            {#each markCategories as category (category.value)}
              <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" checked={opts.sponsorBlockMark.includes(category.value)} onchange={(event) =>
                  (opts.sponsorBlockMark = toggleCategory(
                    opts.sponsorBlockMark,
                    category.value,
                    event.currentTarget.checked
                  ))} /><span>{category.name}</span></label>
            {/each}
          </div>
        </div>
        <div>
          <p class="mb-2 text-sm font-medium text-base-content/80">Cut segments out of the video</p>
          <div class="grid gap-x-6 gap-y-2 sm:grid-cols-2 lg:grid-cols-3">
            {#each sponsorBlockCategories as category (category.value)}
              <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" checked={opts.sponsorBlockRemove.includes(category.value)} onchange={(event) =>
                  (opts.sponsorBlockRemove = toggleCategory(
                    opts.sponsorBlockRemove,
                    category.value,
                    event.currentTarget.checked
                  ))} /><span>{category.name}</span></label>
            {/each}
          </div>
        </div>
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="opt-sb-chapter-title">Chapter title template</label>
            <input class="input w-full" id="opt-sb-chapter-title" bind:value={opts.sponsorBlockChapterTitle} placeholder="[SponsorBlock]: %(category_names)l" />
          </div>
          <div>
            <label class="label mb-2 text-sm" for="opt-sb-api">SponsorBlock API URL</label>
            <input class="input w-full" id="opt-sb-api" type="url" bind:value={opts.sponsorBlockApi} placeholder="https://sponsor.ajay.app" />
          </div>
        </div>
      {/if}
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <h3 class="text-sm font-semibold text-base-content/90">Download behavior & limits</h3>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <div>
        <label class="label mb-2 text-sm" for="opt-limit-rate">Speed limit</label>
        <input class="input w-full" id="opt-limit-rate" bind:value={opts.limitRate} pattern={'\\d+(\\.\\d+)?[KMG]?'} placeholder="e.g. 4.2M" />
        <p class="mt-1.5 text-xs text-base-content/40">Maximum download rate, e.g. 50K or 4.2M.</p>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-concurrent-fragments">Concurrent fragments</label>
        <input class="input w-full" id="opt-concurrent-fragments" type="number" min={1} max={16} bind:value={opts.concurrentFragments} placeholder="Default" />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-retries">Retries</label>
        <input class="input w-full" id="opt-retries" bind:value={opts.retries} placeholder={'e.g. 10 or "infinite"'} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-playlist-items">Playlist items</label>
        <input class="input w-full" id="opt-playlist-items" bind:value={opts.playlistItems} placeholder="e.g. 1:100" />
        <p class="mt-1.5 text-xs text-base-content/40">Which entries to take when the link is a playlist.</p>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-max-filesize">Max file size</label>
        <input class="input w-full" id="opt-max-filesize" bind:value={opts.maxFilesize} placeholder="e.g. 500M" />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-min-filesize">Min file size</label>
        <input class="input w-full" id="opt-min-filesize" bind:value={opts.minFilesize} placeholder="e.g. 1M" />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-date">Uploaded on</label>
        <input class="input w-full" id="opt-date" type="date" bind:value={opts.date} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-date-after">Uploaded on or after</label>
        <input class="input w-full" id="opt-date-after" type="date" bind:value={opts.dateAfter} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-date-before">Uploaded on or before</label>
        <input class="input w-full" id="opt-date-before" type="date" bind:value={opts.dateBefore} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-throttled-rate">Throttled rate threshold</label>
        <input class="input w-full" id="opt-throttled-rate" bind:value={opts.throttledRate} placeholder="e.g. 100K" />
        <p class="mt-1.5 text-xs text-base-content/40">Re-extract the download if speed drops below this, for sites that throttle.</p>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-buffer-size">Buffer size</label>
        <input class="input w-full" id="opt-buffer-size" bind:value={opts.bufferSize} placeholder="e.g. 16K" />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-http-chunk-size">HTTP chunk size</label>
        <input class="input w-full" id="opt-http-chunk-size" bind:value={opts.httpChunkSize} placeholder="e.g. 10M" />
        <p class="mt-1.5 text-xs text-base-content/40">Splits downloads into chunks of this size; helps with some rate limits.</p>
      </div>
      <TriStateSelect id="opt-resize-buffer" label="Resize the download buffer automatically" bind:value={opts.resizeBuffer} />
      <div class="sm:col-span-2">
        <label class="label mb-2 text-sm" for="opt-retry-sleep">Retry sleep expressions</label>
        <textarea class="textarea w-full font-mono" id="opt-retry-sleep" rows={2} bind:value={opts.retrySleep} placeholder={'exp=1:20\nfragment:exp=1:10'}></textarea>
        <p class="mt-1.5 text-xs text-base-content/40">
          One <code>[type:]EXPR</code> per line (types: http, fragment, file_access, extractor). See yt-dlp's
          <code>--retry-sleep</code> docs for the expression syntax.
        </p>
      </div>
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <div>
        <h3 class="text-sm font-semibold text-base-content/90">Network & authentication</h3>
        <p class="mt-1 text-xs text-base-content/40">For sites that need a login or a proxy. Prefer cookie profiles for site logins when possible.</p>
      </div>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 grid gap-4 sm:grid-cols-2">
      <div class="sm:col-span-2">
        <label class="label mb-2 text-sm" for="opt-proxy">Proxy URL</label>
        <input class="input w-full" id="opt-proxy" bind:value={opts.proxy} placeholder="socks5://127.0.0.1:1080" />
        <p class="mt-1.5 text-xs text-base-content/40">Routes the server’s download traffic through this proxy.</p>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-username">Username</label>
        <input class="input w-full" id="opt-username" autocomplete="off" bind:value={opts.username} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-password">Password</label>
        <input class="input w-full" id="opt-password" type="password" autocomplete="new-password" bind:value={opts.password} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-two-factor">Two-factor code</label>
        <input class="input w-full" id="opt-two-factor" autocomplete="off" bind:value={opts.twoFactor} />
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-video-password">Video password</label>
        <input class="input w-full" id="opt-video-password" type="password" autocomplete="new-password" bind:value={opts.videoPassword} />
      </div>
      <p class="text-xs text-warning sm:col-span-2">
        Credentials are stored as plain text in the preset and sent to the site during downloads.
      </p>
    </div>
  </details>

  <details class="group rounded-box border-[length:var(--border)] border-base-300/70 bg-base-200/40 p-4">
    <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
      <div>
        <h3 class="text-sm font-semibold text-base-content/90">Workarounds</h3>
        <p class="mt-1 text-xs text-base-content/40">Only needed for sites that misbehave with the default settings.</p>
      </div>
      <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
    </summary>
    <div class="mt-4 space-y-4">
      <div class="flex flex-wrap gap-x-8 gap-y-3">
        <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle toggle-primary" bind:checked={opts.noCheckCertificates} /><span>Skip certificate checks</span></label>
        <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle toggle-primary" bind:checked={opts.legacyServerConnect} /><span>Allow legacy server connections</span></label>
      </div>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="opt-sleep-requests">Sleep between requests (s)</label>
          <input class="input w-full" id="opt-sleep-requests" type="number" min={0} step="any" bind:value={opts.sleepRequests} placeholder="Default" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="opt-sleep-subtitles">Sleep between subtitles (s)</label>
          <input class="input w-full" id="opt-sleep-subtitles" type="number" min={0} step="any" bind:value={opts.sleepSubtitles} placeholder="Default" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="opt-sleep-interval">Sleep before download (s)</label>
          <input class="input w-full" id="opt-sleep-interval" type="number" min={0} step="any" bind:value={opts.sleepInterval} placeholder="Default" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="opt-max-sleep-interval">Max sleep before download (s)</label>
          <input class="input w-full" id="opt-max-sleep-interval" type="number" min={0} step="any" bind:value={opts.maxSleepInterval} placeholder="Default" />
        </div>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="opt-add-headers">Extra HTTP headers</label>
        <textarea class="textarea w-full font-mono" id="opt-add-headers" rows={3} bind:value={opts.addHeaders} placeholder={'Referer: https://example.com\nX-Custom: value'}></textarea>
        <p class="mt-1.5 text-xs text-base-content/40">One <code>Header: value</code> per line.</p>
      </div>
    </div>
  </details>
</div>
