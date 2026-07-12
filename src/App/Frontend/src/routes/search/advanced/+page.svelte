<script lang="ts">
  import { goto } from '$app/navigation';
  import { page as pageState } from '$app/state';
  import { Button, Input, Label, Select } from 'flowbite-svelte';
  import { SearchOutline, RefreshOutline } from 'flowbite-svelte-icons';
  import type { SearchScope } from '$lib/api/search';

  // Structured builder over the DataBridge advanced-query syntax (AdvancedQueryParser).
  // Every control maps to one or more `field:value` tokens that the /search page already understands,
  // so this page is purely a query composer — it never calls the search API itself.

  const inputClass =
    'w-full! border-slate-800! bg-slate-900/80! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const labelClass = 'mb-1.5 text-xs font-medium text-slate-400';

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
      <h1 id="advanced-search-title" class="text-2xl font-bold tracking-tight text-white">Advanced search</h1>
      <p class="mt-1 text-sm text-slate-500">
        Fill in any fields to build a search. Everything maps to the
        <code class="rounded bg-slate-800/80 px-1.5 py-0.5 font-mono text-xs text-slate-400">field:value</code>
        syntax you can also type directly in the search bar.
      </p>
    </div>
    <a
      href="/search"
      class="text-sm font-medium text-slate-400 transition hover:text-slate-200 hover:underline"
    >
      ← Back to search
    </a>
  </div>

  <form class="mt-6 space-y-6" onsubmit={runSearch}>
    <!-- Creator & taxonomy -->
    <fieldset class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Creator & tags</legend>
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <div>
          <Label class={labelClass}>Channel / creator</Label>
          <Input bind:value={channel} placeholder="e.g. Linus Tech Tips" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Platform</Label>
          <Input bind:value={platform} placeholder="e.g. youtube" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Language</Label>
          <Input bind:value={language} placeholder="e.g. en" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Tag</Label>
          <Input bind:value={tag} placeholder="e.g. review" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Category</Label>
          <Input bind:value={category} placeholder="e.g. Gaming" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Genre</Label>
          <Input bind:value={genre} placeholder="e.g. Rock" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Artist</Label>
          <Input bind:value={artist} placeholder="e.g. Daft Punk" class={inputClass} />
        </div>
      </div>
    </fieldset>

    <!-- Technical -->
    <fieldset class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Technical</legend>
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <Label class={labelClass}>Codec</Label>
          <Select items={codecOptions} bind:value={codec} class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Resolution</Label>
          <Select items={resolutionOptions} bind:value={resolution} class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>HDR</Label>
          <Select items={hdrOptions} bind:value={hdr} class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Audio channels</Label>
          <Select items={audioOptions} bind:value={audio} class={inputClass} />
        </div>
      </div>
    </fieldset>

    <!-- Ranges -->
    <fieldset class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Dates & ranges</legend>
      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <Label class={labelClass}>Released after</Label>
          <Input bind:value={after} placeholder="year or date, e.g. 2023" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Released before</Label>
          <Input bind:value={before} placeholder="year or date, e.g. 2024-06-01" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Duration (seconds)</Label>
          <div class="flex items-center gap-2">
            <Input type="number" min="0" bind:value={durationMin} placeholder="min" class={inputClass} />
            <span class="text-slate-600">–</span>
            <Input type="number" min="0" bind:value={durationMax} placeholder="max" class={inputClass} />
          </div>
        </div>
        <div>
          <Label class={labelClass}>Views</Label>
          <div class="flex items-center gap-2">
            <Input type="number" min="0" bind:value={viewsMin} placeholder="min" class={inputClass} />
            <span class="text-slate-600">–</span>
            <Input type="number" min="0" bind:value={viewsMax} placeholder="max" class={inputClass} />
          </div>
        </div>
        <div>
          <Label class={labelClass}>Likes</Label>
          <div class="flex items-center gap-2">
            <Input type="number" min="0" bind:value={likesMin} placeholder="min" class={inputClass} />
            <span class="text-slate-600">–</span>
            <Input type="number" min="0" bind:value={likesMax} placeholder="max" class={inputClass} />
          </div>
        </div>
      </div>
    </fieldset>

    <!-- Free text & scope -->
    <fieldset class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
      <legend class="px-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Keywords & scope</legend>
      <div class="grid gap-4 sm:grid-cols-3">
        <div class="sm:col-span-2">
          <Label class={labelClass}>Free text</Label>
          <Input bind:value={freeText} placeholder="Any additional keywords" class={inputClass} />
        </div>
        <div>
          <Label class={labelClass}>Search in</Label>
          <Select items={scopeOptions} bind:value={scope} class={inputClass} />
        </div>
      </div>
    </fieldset>

    <!-- Preview + actions -->
    <div class="rounded-2xl border border-slate-800/80 bg-slate-950/50 p-4">
      <p class="text-xs font-medium text-slate-500">Query preview</p>
      <div class="mt-2 flex flex-wrap items-center justify-between gap-4">
        <code class="min-w-0 break-all font-mono text-sm text-blue-300/90">
          {builtQuery || 'Nothing selected yet'}
        </code>
        <div class="flex shrink-0 gap-2">
          <Button
            type="button"
            color="dark"
            onclick={reset}
            class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
          >
            <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
            Reset
          </Button>
          <Button
            type="submit"
            color="blue"
            disabled={!canSearch}
            class="border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! text-white! hover:bg-blue-400! disabled:opacity-40"
          >
            <SearchOutline class="mr-1.5 h-3.5 w-3.5" />
            Search
          </Button>
        </div>
      </div>
    </div>
  </form>

  <!-- Prefix key reference -->
  <div class="mt-8 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
    <h2 class="text-sm font-bold text-slate-100">Available prefix keys</h2>
    <p class="mt-1 text-sm text-slate-500">
      Type any of these directly in the search bar, or click an example to add it to the free-text box.
      Unknown prefixes fall back to plain keywords. Wrap values with spaces in quotes.
    </p>
    <div class="mt-4 overflow-x-auto">
      <table class="w-full border-collapse text-left text-sm">
        <thead>
          <tr class="border-b border-slate-800 text-xs uppercase tracking-wide text-slate-500">
            <th class="py-2 pr-4 font-medium">Key(s)</th>
            <th class="py-2 pr-4 font-medium">Matches</th>
            <th class="py-2 font-medium">Example</th>
          </tr>
        </thead>
        <tbody>
          {#each prefixReference as row (row.keys)}
            <tr class="border-b border-slate-800/50 align-top">
              <td class="py-2 pr-4 font-mono text-xs text-slate-300">{row.keys}</td>
              <td class="py-2 pr-4 text-slate-400">{row.description}</td>
              <td class="py-2">
                <button
                  type="button"
                  onclick={() => applyExample(row.example)}
                  class="rounded bg-slate-800/80 px-1.5 py-0.5 font-mono text-xs text-blue-300/90 transition hover:bg-slate-700/80"
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
