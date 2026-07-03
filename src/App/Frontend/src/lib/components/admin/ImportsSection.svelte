<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Helper, Input, Label, Select, Spinner } from 'flowbite-svelte';
  import {
    CheckCircleOutline,
    ChevronDownOutline,
    ChevronUpOutline,
    ExclamationCircleOutline,
    FileImportOutline
  } from 'flowbite-svelte-icons';
  import { submitLocalMediaImport, type LocalMediaImportReceipt } from '$lib/api/imports';
  import { listStorage } from '$lib/api/storage';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';

  interface SubmittedBatch extends LocalMediaImportReceipt {
    manifestName: string;
    sourceRoot: string;
    storageKey: string;
    submittedAt: Date;
  }

  let manifestFiles = $state<FileList | undefined>();
  let sourceRoot = $state('');
  let storageKey = $state('');
  let requestedBy = $state('');
  let submitBusy = $state(false);
  let submitError = $state<string | null>(null);
  let submitted = $state<SubmittedBatch[]>([]);
  let manifestHelpOpen = $state(false);

  let storageKeys = $state<string[]>([]);
  let storageKeysError = $state<string | null>(null);

  const manifest = $derived(manifestFiles?.item(0) ?? null);
  const storageItems = $derived(storageKeys.map((key) => ({ value: key, name: key })));

  const manifestExample = `{
  "items": [
    {
      "file": "channel/video-01.mkv",
      "provider": "youtube",
      "sourceMediaId": "dQw4w9WgXcQ",
      "sourceUrl": "https://youtube.com/watch?v=dQw4w9WgXcQ",
      "title": "My archived video",
      "sidecars": {
        "infoJson": "channel/video-01.info.json",
        "thumbnail": "channel/video-01.jpg",
        "captions": [
          { "file": "channel/video-01.en.vtt", "languageCode": "en", "captionType": "manual" }
        ]
      }
    }
  ]
}`;

  onMount(() => {
    void loadStorageKeys();
  });

  async function loadStorageKeys() {
    try {
      const targets = await listStorage();
      storageKeys = targets.map((target) => target.key);
      if (!storageKey && storageKeys.length > 0) {
        storageKey = storageKeys.includes('default') ? 'default' : storageKeys[0];
      }
    } catch (err) {
      storageKeysError = err instanceof Error ? err.message : 'Could not load storage targets.';
    }
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!manifest) {
      submitError = 'Choose a manifest file first.';
      return;
    }
    if (!sourceRoot.trim()) {
      submitError = 'Enter the source root directory.';
      return;
    }
    if (!storageKey.trim()) {
      submitError = 'Choose a destination storage target.';
      return;
    }

    submitBusy = true;
    submitError = null;
    try {
      const receipt = await submitLocalMediaImport({
        manifest,
        sourceRoot: sourceRoot.trim(),
        storageKey: storageKey.trim(),
        requestedBy: requestedBy.trim() || undefined
      });
      submitted = [
        {
          ...receipt,
          manifestName: manifest.name,
          sourceRoot: sourceRoot.trim(),
          storageKey: storageKey.trim(),
          submittedAt: new Date()
        },
        ...submitted
      ];
      manifestFiles = undefined;
      requestedBy = '';
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Could not submit the import.';
    } finally {
      submitBusy = false;
    }
  }
</script>

<section class={cardClass} aria-labelledby="imports-title">
  <h2 id="imports-title" class="text-base font-bold text-slate-100">Import local media</h2>
  <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
    Queue a background import of media files that already exist on a worker's filesystem. Upload a JSON manifest
    describing the files, point at the directory they live under, and pick the storage target they should be copied
    into. The import runs asynchronously — this page only hands the batch off.
  </p>

  {#if submitError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{submitError}</span>
    </div>
  {/if}

  <form onsubmit={submit} class="mt-5 space-y-4">
    <div class="grid gap-4 sm:grid-cols-2">
      <div class="min-w-0">
        <Label for="import-manifest" class="mb-2 text-sm font-medium text-slate-300">Manifest (JSON)</Label>
        <input
          id="import-manifest"
          type="file"
          accept=".json,application/json"
          bind:files={manifestFiles}
          class="block w-full cursor-pointer rounded-lg border border-slate-800 bg-slate-950/60 text-sm text-slate-400 file:mr-3 file:cursor-pointer file:rounded-l-lg file:border-0 file:bg-slate-800 file:px-3 file:py-2.5 file:text-xs file:font-semibold file:text-slate-200 hover:file:bg-slate-700"
        />
        <Helper class="mt-1.5 text-xs! text-slate-500!">Lists the files to import, with optional sidecars.</Helper>
      </div>

      <div class="min-w-0">
        <Label for="import-storage-key" class="mb-2 text-sm font-medium text-slate-300">Destination storage</Label>
        {#if storageKeys.length > 0}
          <Select
            id="import-storage-key"
            items={storageItems}
            bind:value={storageKey}
            placeholder="Choose a storage target"
            class={inputClass}
          />
        {:else}
          <Input id="import-storage-key" bind:value={storageKey} placeholder="default" class={inputClass} />
        {/if}
        {#if storageKeysError}
          <Helper class="mt-1.5 text-xs! text-amber-300!">
            {storageKeysError} Enter the storage key manually.
          </Helper>
        {:else}
          <Helper class="mt-1.5 text-xs! text-slate-500!">Storage target the files are copied into.</Helper>
        {/if}
      </div>

      <div class="min-w-0">
        <Label for="import-source-root" class="mb-2 text-sm font-medium text-slate-300">Source root</Label>
        <Input
          id="import-source-root"
          bind:value={sourceRoot}
          placeholder="/mnt/archive/incoming"
          class="{inputClass} font-mono!"
        />
        <Helper class="mt-1.5 text-xs! text-slate-500!">
          Directory on the worker that manifest file paths are relative to.
        </Helper>
      </div>

      <div class="min-w-0">
        <Label for="import-requested-by" class="mb-2 text-sm font-medium text-slate-300">Note (optional)</Label>
        <Input
          id="import-requested-by"
          bind:value={requestedBy}
          placeholder="2019 channel backfill"
          class={inputClass}
        />
        <Helper class="mt-1.5 text-xs! text-slate-500!">Recorded alongside the batch for later reference.</Helper>
      </div>
    </div>

    <Button
      type="submit"
      color="blue"
      class="border-0! px-4! py-2.5! text-xs! font-semibold! disabled:opacity-60"
      disabled={submitBusy}
    >
      {#if submitBusy}
        <Spinner size="4" class="mr-1.5" />
      {:else}
        <FileImportOutline class="mr-1.5 h-4 w-4" />
      {/if}
      Queue import
    </Button>
  </form>

  <button
    type="button"
    onclick={() => (manifestHelpOpen = !manifestHelpOpen)}
    class="mt-5 flex items-center gap-1 text-xs font-semibold text-slate-500 transition hover:text-slate-300"
  >
    Manifest format
    {#if manifestHelpOpen}
      <ChevronUpOutline class="h-3 w-3" />
    {:else}
      <ChevronDownOutline class="h-3 w-3" />
    {/if}
  </button>
  {#if manifestHelpOpen}
    <div class="mt-2 rounded-lg border border-slate-800 bg-slate-950/60 p-3">
      <p class="text-xs text-slate-400">
        Each item's <code class="text-slate-300">file</code> path is resolved against the source root. Everything
        except <code class="text-slate-300">file</code> is optional: provider and source IDs link the media back to its
        origin, and sidecars attach an info.json, thumbnail, and caption files.
      </p>
      <pre class="mt-2 overflow-x-auto rounded bg-black/40 p-2.5 font-mono text-xs leading-5 text-slate-300">{manifestExample}</pre>
    </div>
  {/if}
</section>

<section class={cardClass} aria-labelledby="imports-submitted-title">
  <h2 id="imports-submitted-title" class="text-base font-bold text-slate-100">Queued this session</h2>
  <p class="mt-2 text-sm text-slate-400">
    Batches handed off from this page. Progress is tracked by the background import pipeline; this list clears when you
    leave the page.
  </p>

  {#if submitted.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <FileImportOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No imports queued yet</p>
      <p class="mt-1 text-sm text-slate-500">Submit a manifest above to hand a batch to the import pipeline.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each submitted as batch (batch.batchId)}
        <article class="rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 sm:px-4">
          <div class="flex flex-wrap items-center gap-2">
            <CheckCircleOutline class="h-4 w-4 shrink-0 text-emerald-400" />
            <span class="text-sm font-semibold text-slate-100">{batch.manifestName}</span>
            <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
              {batch.storageKey}
            </span>
            <span class="ml-auto text-xs text-slate-500">{batch.submittedAt.toLocaleTimeString()}</span>
          </div>
          <p class="mt-1.5 truncate font-mono text-xs text-slate-400" title={batch.sourceRoot}>{batch.sourceRoot}</p>
          <p class="mt-1 font-mono text-[10px] text-slate-600">
            batch {batch.batchId} · correlation {batch.correlationId}
          </p>
        </article>
      {/each}
    </div>
  {/if}
</section>
