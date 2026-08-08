<script lang="ts">
  import { goto } from '$app/navigation';
  import { page as pageState } from '$app/state';
  import { Select } from '$lib/components/ui';
  import { RefreshCw, Search } from '@lucide/svelte';
  import type { SearchScope } from '$lib/api/search';

  // Structured builder over the DataBridge advanced-query syntax (AdvancedQueryParser).
  // Every control maps to one or more `field:value` tokens that the /search page already understands,
  // so this page is purely a query composer — it never calls the search API itself.

  const labelClass = 'mb-1.5 text-xs font-medium text-base-content/60';

  const codecOptions = [
    { value: '', name: 'Any codec' },
    { value: 'h264', name: 'H.264 / AVC' },
    { value: 'hevc', name: 'HEVC / H.265' },
    { value: 'av1', name: 'AV1' },
    { value: 'vp9', name: 'VP9' },
    { value: 'aac', name: 'AAC (audio)' },
    { value: 'opus', name: 'Opus (audio)' },
    { value: 'mp3', name: 'MP3 (audio)' }
  ];

  const resolutionOptions = [
    { value: '', name: 'Any resolution' },
    { value: '2160p', name: '2160p · 4K UHD' },
    { value: '1440p', name: '1440p · 2K QHD' },
    { value: '1080p', name: '1080p · FHD' },
    { value: '720p', name: '720p · HD' },
    { value: '480p', name: '480p' },
    { value: 'sd', name: 'SD' }
  ];

  const hdrOptions = [
    { value: '', name: 'Any' },
    { value: 'true', name: 'HDR only' },
    { value: 'false', name: 'SDR only' }
  ];

  const audioOptions = [
    { value: '', name: 'Any' },
    { value: 'mono', name: 'Mono' },
    { value: 'stereo', name: 'Stereo · 2.0' },
    { value: '2.1', name: '2.1' },
    { value: '5.1', name: '5.1 surround' },
    { value: '7.1', name: '7.1 surround' }
  ];

  const scopeOptions: { value: SearchScope; name: string }[] = [
    { value: 'all', name: 'Everything' },
    { value: 'metadata', name: 'Title & metadata' },
    { value: 'subtitles', name: 'Subtitles' },
    { value: 'comments', name: 'Comments' }
  ];

  // Reference of every prefix key exposed by the parser, grouped for the help panel.
  const prefixReference: { keys: string; description: string; example: string }[] = [
    { keys: 'channel: · creator: · uploader:', description: 'Channel name or @handle', example: 'channel:"Linus Tech Tips"' },
    { keys: 'platform:', description: 'Source platform', example: 'platform:youtube' },
    { keys: 'tag:', description: 'Media tag', example: 'tag:review' },
    { keys: 'category:', description: 'Category', example: 'category:Gaming' },
    { keys: 'genre:', description: 'Genre', example: 'genre:Rock' },
    { keys: 'artist:', description: 'Artist', example: 'artist:"Daft Punk"' },
    { keys: 'lang: · language: · subtitle:', description: 'Caption/subtitle language code', example: 'lang:en' },
    { keys: 'codec:', description: 'Video or audio codec (h265/x265 → hevc)', example: 'codec:h264' },
    { keys: 'resolution: · res:', description: '2160p/4k, 1440p/2k, 1080p, 720p, 480p, sd', example: 'resolution:1080p' },
    { keys: 'hdr:', description: 'true/hdr/hdr10/dv or false/sdr', example: 'hdr:true' },
    { keys: 'audio: · channels:', description: 'mono, stereo/2.0, 2.1, 5.1, 7.1, or a channel count', example: 'audio:5.1' },
    { keys: 'after:', description: 'Released after a year or date', example: 'after:2023' },
    { keys: 'before:', description: 'Released before a year or date', example: 'before:2024-06-01' },
    { keys: 'duration:', description: 'Length in seconds; supports > < >= <= =', example: 'duration:>600' },
    { keys: 'views: · view_count:', description: 'View count; supports > < >= <= =', example: 'views:>=100000' },
    { keys: 'likes: · like_count:', description: 'Like count; supports > < >= <= =', example: 'likes:>1000' }
  ];

  // Form state.
  let channel = $state('');
  let platform = $state('');
  let tag = $state('');
  let category = $state('');
  let genre = $state('');
  let artist = $state('');
  let language = $state('');
  let codec = $state('');
  let resolution = $state('');
  let hdr = $state('');
  let audio = $state('');
  let after = $state('');
  let before = $state('');
  let durationMin = $state('');
  let durationMax = $state('');
  let viewsMin = $state('');
  let viewsMax = $state('');
  let likesMin = $state('');
  let likesMax = $state('');
  let freeText = $state('');
  let scope = $state<SearchScope>('all');

  // A single `field:value` token; quotes values containing whitespace so the tokenizer keeps them intact.
  function token(prefix: string, value: string): string | null {
    const trimmed = value.trim().replaceAll('"', '');
    if (!trimmed) return null;
    return /\s/.test(trimmed) ? `${prefix}:"${trimmed}"` : `${prefix}:${trimmed}`;
  }

  // A numeric bound token, e.g. duration:>=600. Ignores non-numeric input.
  function rangeToken(prefix: string, op: string, value: string): string | null {
    const trimmed = value.trim();
    if (!trimmed || !/^\d+$/.test(trimmed)) return null;
    return `${prefix}:${op}${trimmed}`;
  }

  const queryTokens = $derived(
    [
      token('channel', channel),
      token('platform', platform),
      token('tag', tag),
      token('category', category),
      token('genre', genre),
      token('artist', artist),
      token('lang', language),
      codec ? `codec:${codec}` : null,
      resolution ? `resolution:${resolution}` : null,
      hdr ? `hdr:${hdr}` : null,
      audio ? `audio:${audio}` : null,
      token('after', after),
      token('before', before),
      rangeToken('duration', '>=', durationMin),
      rangeToken('duration', '<=', durationMax),
      rangeToken('views', '>=', viewsMin),
      rangeToken('views', '<=', viewsMax),
      rangeToken('likes', '>=', likesMin),
      rangeToken('likes', '<=', likesMax),
      freeText.trim() || null
    ].filter((part): part is string => Boolean(part))
  );

  const builtQuery = $derived(queryTokens.join(' '));
  const canSearch = $derived(queryTokens.length > 0);

  function runSearch(event: SubmitEvent) {
    event.preventDefault();
    if (!canSearch) return;
    const params = new URLSearchParams({ q: builtQuery });
    if (scope !== 'all') params.set('scope', scope);
    void goto(`/search?${params}`);
  }

  function reset() {
    channel = platform = tag = category = genre = artist = language = '';
    codec = resolution = hdr = audio = after = before = '';
    durationMin = durationMax = viewsMin = viewsMax = likesMin = likesMax = '';
    freeText = '';
    scope = 'all';
  }

  function applyExample(example: string) {
    freeText = freeText.trim() ? `${freeText.trim()} ${example}` : example;
  }

  // Preserve any q= a user arrived with (e.g. "refine this query") as free text.
  const initialQuery = pageState.url.searchParams.get('q')?.trim() ?? '';
  if (initialQuery) freeText = initialQuery;
</script>

<svelte:head>
  <title>Advanced search · FrostStream</title>
</svelte:head>

<section aria-labelledby="advanced-search-title" class="mx-auto max-w-5xl">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div class="min-w-0">
      <h1 id="advanced-search-title" class="text-2xl font-bold tracking-tight text-base-content">Advanced search</h1>
      <p class="mt-1 text-sm text-base-content/50">
        Fill in any fields to build a search. Everything maps to the
        <code class="rounded bg-base-300/80 px-1.5 py-0.5 font-mono text-xs text-base-content/60">field:value</code>
        syntax you can also type directly in the search bar.
      </p>
    </div>
    <a
      href="/search"
      class="btn btn-sm btn-neutral text-xs"
    >
      ← Back to search
    </a>
  </div>

  <form class="mt-6 space-y-6" onsubmit={runSearch}>
    <!-- Creator & taxonomy -->
    <fieldset class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-base-content/50">Creator & tags</legend>
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <div>
          <label class="label {labelClass}" for="adv-channel-creator">Channel / creator</label>
          <input id="adv-channel-creator" class="input w-full" bind:value={channel} placeholder="e.g. Linus Tech Tips" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-platform">Platform</label>
          <input id="adv-platform" class="input w-full" bind:value={platform} placeholder="e.g. youtube" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-language">Language</label>
          <input id="adv-language" class="input w-full" bind:value={language} placeholder="e.g. en" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-tag">Tag</label>
          <input id="adv-tag" class="input w-full" bind:value={tag} placeholder="e.g. review" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-category">Category</label>
          <input id="adv-category" class="input w-full" bind:value={category} placeholder="e.g. Gaming" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-genre">Genre</label>
          <input id="adv-genre" class="input w-full" bind:value={genre} placeholder="e.g. Rock" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-artist">Artist</label>
          <input id="adv-artist" class="input w-full" bind:value={artist} placeholder="e.g. Daft Punk" />
        </div>
      </div>
    </fieldset>

    <!-- Technical -->
    <fieldset class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-base-content/50">Technical</legend>
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <label class="label {labelClass}" for="adv-codec">Codec</label>
          <Select id="adv-codec" items={codecOptions} bind:value={codec} />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-resolution">Resolution</label>
          <Select id="adv-resolution" items={resolutionOptions} bind:value={resolution} />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-hdr">HDR</label>
          <Select id="adv-hdr" items={hdrOptions} bind:value={hdr} />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-audio-channels">Audio channels</label>
          <Select id="adv-audio-channels" items={audioOptions} bind:value={audio} />
        </div>
      </div>
    </fieldset>

    <!-- Ranges -->
    <fieldset class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-base-content/50">Dates & ranges</legend>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label class="label {labelClass}" for="adv-released-after">Released after</label>
          <input id="adv-released-after" class="input w-full" bind:value={after} placeholder="year or date, e.g. 2023" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-released-before">Released before</label>
          <input id="adv-released-before" class="input w-full" bind:value={before} placeholder="year or date, e.g. 2024-06-01" />
        </div>
        <div>
          <span class="label {labelClass} block">Duration (seconds)</span>
          <div class="flex items-center gap-2">
            <input class="input w-full" type="number" min="0" bind:value={durationMin} aria-label="Minimum duration" placeholder="min" />
            <span class="text-base-content/40">–</span>
            <input class="input w-full" type="number" min="0" bind:value={durationMax} aria-label="Maximum duration" placeholder="max" />
          </div>
        </div>
        <div>
          <span class="label {labelClass} block">Views</span>
          <div class="flex items-center gap-2">
            <input class="input w-full" type="number" min="0" bind:value={viewsMin} aria-label="Minimum views" placeholder="min" />
            <span class="text-base-content/40">–</span>
            <input class="input w-full" type="number" min="0" bind:value={viewsMax} aria-label="Maximum views" placeholder="max" />
          </div>
        </div>
        <div>
          <span class="label {labelClass} block">Likes</span>
          <div class="flex items-center gap-2">
            <input class="input w-full" type="number" min="0" bind:value={likesMin} aria-label="Minimum likes" placeholder="min" />
            <span class="text-base-content/40">–</span>
            <input class="input w-full" type="number" min="0" bind:value={likesMax} aria-label="Maximum likes" placeholder="max" />
          </div>
        </div>
      </div>
    </fieldset>

    <!-- Free text & scope -->
    <fieldset class="rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-base-content/50">Keywords & scope</legend>
      <div class="grid gap-4 sm:grid-cols-3">
        <div class="sm:col-span-2">
          <label class="label {labelClass}" for="adv-free-text">Free text</label>
          <input id="adv-free-text" class="input w-full" bind:value={freeText} placeholder="Any additional keywords" />
        </div>
        <div>
          <label class="label {labelClass}" for="adv-search-in">Search in</label>
          <Select id="adv-search-in" items={scopeOptions} bind:value={scope} />
        </div>
      </div>
    </fieldset>

    <!-- Preview + actions -->
    <div class="rounded-2xl border border-base-300/80 bg-base-200/50 p-4">
      <p class="text-xs font-medium text-base-content/50">Query preview</p>
      <div class="mt-2 flex flex-wrap items-center justify-between gap-4">
        <code class="min-w-0 break-all font-mono text-sm text-base-content">
          {builtQuery || 'Nothing selected yet'}
        </code>
        <div class="flex shrink-0 gap-2">
          <button class="btn btn-sm btn-neutral text-xs" type="button" onclick={reset}>
            <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
            Reset
          </button>
          <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={!canSearch}>
            <Search class="mr-1.5 h-3.5 w-3.5" />
            Search
          </button>
        </div>
      </div>
    </div>
  </form>

  <!-- Prefix key reference -->
  <div class="mt-8 rounded-2xl border border-base-300/80 bg-base-200/40 p-5">
    <h2 class="text-sm font-bold text-base-content">Available prefix keys</h2>
    <p class="mt-1 text-sm text-base-content/50">
      Type any of these directly in the search bar, or click an example to add it to the free-text box.
      Unknown prefixes fall back to plain keywords. Wrap values with spaces in quotes.
    </p>
    <div class="mt-4 overflow-x-auto">
      <table class="w-full border-collapse text-left text-sm">
        <thead>
          <tr class="border-b border-base-300 text-xs uppercase tracking-wide text-base-content/50">
            <th class="py-2 pr-4 font-medium">Key(s)</th>
            <th class="py-2 pr-4 font-medium">Matches</th>
            <th class="py-2 font-medium">Example</th>
          </tr>
        </thead>
        <tbody>
          {#each prefixReference as row (row.keys)}
            <tr class="border-b border-base-300/50 align-top">
              <td class="py-2 pr-4 font-mono text-xs text-base-content/80">{row.keys}</td>
              <td class="py-2 pr-4 text-base-content/60">{row.description}</td>
              <td class="py-2">
                <button
                  type="button"
                  onclick={() => applyExample(row.example)}
                  class="rounded bg-base-300/80 px-1.5 py-0.5 font-mono text-xs text-primary transition hover:bg-base-300/80"
                  title="Add to free text"
                >
                  {row.example}
                </button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
</section>
