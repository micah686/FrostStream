<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Helper, Input, Label, Select, Spinner } from 'flowbite-svelte';
  import { ExclamationCircleOutline, FileImportOutline, RefreshOutline } from 'flowbite-svelte-icons';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
  import { createImportSession, listImportSessions, type ImportSession } from '$lib/api/imports';
  import { listStorage } from '$lib/api/storage';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';

  let storageKey = $state('');
  let workerTag = $state('');
  let subPath = $state('');
  let note = $state('');
  let busy = $state(false);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let sessions = $state<ImportSession[]>([]);
  let storageKeys = $state<string[]>([]);

  const storageItems = $derived(storageKeys.map((key) => ({ value: key, name: key })));

  onMount(() => {
    void load();
    void loadStorageKeys();
  });

  async function loadStorageKeys() {
    try {
      const targets = await listStorage();
      storageKeys = targets.map((target) => target.key);
      if (!storageKey && storageKeys.length > 0) storageKey = storageKeys.includes('default') ? 'default' : storageKeys[0];
    } catch {
      // Manual entry remains available.
    }
  }

  async function load() {
    loading = true;
    error = null;
    try {
      sessions = (await listImportSessions()).items;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load import sessions.';
    } finally {
      loading = false;
    }
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!storageKey.trim()) {
      error = 'Choose a destination storage target.';
      return;
    }

    busy = true;
    error = null;
    try {
      const session = await createImportSession({
        storageKey: storageKey.trim(),
        workerTag: workerTag.trim() || undefined,
        subPath: subPath.trim() || undefined,
        requestedBy: note.trim() || undefined
      });
      sessions = [session, ...sessions.filter((item) => item.sessionId !== session.sessionId)];
      note = '';
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not start the scan.';
    } finally {
      busy = false;
    }
  }

  function formatDate(value: string) {
    return new Date(value).toLocaleString();
  }
</script>

<UnderDevelopmentBanner />

<section class={cardClass} aria-labelledby="imports-title">
  <div class="flex flex-wrap items-start gap-3">
    <div>
      <h2 id="imports-title" class="text-base font-bold text-slate-100">Import local media</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Start a scan of a worker's incoming folder, review discovered items, then commit only the files with approved
        metadata.
      </p>
    </div>
    <Button
      color="dark"
      class="ml-auto border-slate-700! bg-slate-900! px-3! py-2! text-xs!"
      onclick={load}
      disabled={loading}
    >
      <RefreshOutline class="mr-1.5 h-4 w-4" />
      Refresh
    </Button>
  </div>

  {#if error}
    <div class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error}</span>
    </div>
  {/if}

  <form onsubmit={submit} class="mt-5 grid gap-4 lg:grid-cols-[1fr_1fr_1fr_auto]">
    <div>
      <Label for="import-storage-key" class="mb-2 text-sm font-medium text-slate-300">Destination storage</Label>
      {#if storageKeys.length > 0}
        <Select id="import-storage-key" items={storageItems} bind:value={storageKey} class={inputClass} />
      {:else}
        <Input id="import-storage-key" bind:value={storageKey} placeholder="default" class={inputClass} />
      {/if}
      <Helper class="mt-1.5 text-xs! text-slate-500!">Files are copied here after review.</Helper>
    </div>

    <div>
      <Label for="import-worker-tag" class="mb-2 text-sm font-medium text-slate-300">Worker tag</Label>
      <Input id="import-worker-tag" bind:value={workerTag} placeholder="nas" class="{inputClass} font-mono!" />
      <Helper class="mt-1.5 text-xs! text-slate-500!">Optional scanner/ingest worker.</Helper>
    </div>

    <div>
      <Label for="import-sub-path" class="mb-2 text-sm font-medium text-slate-300">Incoming sub-path</Label>
      <Input id="import-sub-path" bind:value={subPath} placeholder="channel-archive" class={inputClass} />
      <Helper class="mt-1.5 text-xs! text-slate-500!">Optional folder under incoming.</Helper>
    </div>

    <div class="flex items-end">
      <Button type="submit" color="blue" class="w-full border-0! px-4! py-2.5! text-xs! font-semibold!" disabled={busy}>
        {#if busy}
          <Spinner size="4" class="mr-1.5" />
        {:else}
          <FileImportOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Scan
      </Button>
    </div>

    <div class="lg:col-span-3">
      <Label for="import-note" class="mb-2 text-sm font-medium text-slate-300">Note</Label>
      <Input id="import-note" bind:value={note} placeholder="2019 channel backfill" class={inputClass} />
    </div>
  </form>
</section>

<section class={cardClass} aria-labelledby="sessions-title">
  <h2 id="sessions-title" class="text-base font-bold text-slate-100">Import sessions</h2>

  {#if loading && sessions.length === 0}
    <div class="mt-5 flex items-center gap-2 text-sm text-slate-400">
      <Spinner size="4" />
      Loading sessions
    </div>
  {:else if sessions.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <FileImportOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No import sessions</p>
      <p class="mt-1 text-sm text-slate-500">Start a scan to create the review queue.</p>
    </div>
  {:else}
    <div class="mt-5 overflow-hidden rounded-xl border border-slate-800">
      <table class="min-w-full divide-y divide-slate-800 text-left text-sm">
        <thead class="bg-slate-950/60 text-xs uppercase tracking-wide text-slate-500">
          <tr>
            <th class="px-4 py-3">Session</th>
            <th class="px-4 py-3">Status</th>
            <th class="px-4 py-3">Items</th>
            <th class="px-4 py-3">Storage</th>
            <th class="px-4 py-3">Updated</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-800 bg-[#151a26]">
          {#each sessions as session (session.sessionId)}
            <tr class="hover:bg-slate-900/40">
              <td class="px-4 py-3">
                <a class="font-mono text-xs font-semibold text-blue-300 hover:text-blue-200" href={`/admin/import/${session.sessionId}`}>
                  {session.sessionId}
                </a>
                {#if session.subPath}
                  <p class="mt-1 text-xs text-slate-500">{session.subPath}</p>
                {/if}
              </td>
              <td class="px-4 py-3 text-slate-300">{session.status}</td>
              <td class="px-4 py-3 text-slate-300">
                {session.totalItems}
                <span class="text-slate-500">total</span>
                <span class="ml-2 text-amber-300">{session.incompleteItems}</span>
                <span class="text-slate-500">incomplete</span>
              </td>
              <td class="px-4 py-3 font-mono text-xs text-slate-400">{session.storageKey}</td>
              <td class="px-4 py-3 text-xs text-slate-500">{formatDate(session.updatedAt)}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</section>
