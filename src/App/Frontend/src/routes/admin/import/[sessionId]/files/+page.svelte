<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { Button, Checkbox, Input, Spinner, Toggle } from 'flowbite-svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { bulkImportSessionItems, getImportSession, listImportSessionItems, updateImportSessionOptions, type ImportSession, type ImportSessionItem } from '$lib/api/imports';

  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const sessionId = $derived(page.params.sessionId ?? '');
  let session = $state<ImportSession | null>(null); let available = $state<ImportSessionItem[]>([]); let selected = $state<ImportSessionItem[]>([]);
  let availableNext = $state<string | null | undefined>(null); let selectedNext = $state<string | null | undefined>(null);
  let availableCount = $state(0); let selectedCount = $state(0); let search = $state(''); let checkedAvailable = $state<string[]>([]); let checkedSelected = $state<string[]>([]);
  let loading = $state(false); let actionBusy = $state(false); let error = $state<string | null>(null); let pollTimer: ReturnType<typeof setTimeout> | undefined;

  onMount(() => { void load(); return () => { if (pollTimer) clearTimeout(pollTimer); }; });
  async function load(append: 'available' | 'selected' | null = null) {
    loading = true; error = null;
    try {
      session = await getImportSession(sessionId);
      if (session.status === 'scanning') { pollTimer = setTimeout(() => void load(), 1500); return; }
      if (session.status === 'scanFailed') { error = session.errorMessage || 'The folder scan failed.'; return; }
      const [a, s] = await Promise.all([
        listImportSessionItems(sessionId, { included: false, search: search.trim() || undefined, afterItemId: append === 'available' ? availableNext || undefined : undefined, limit: 50 }),
        listImportSessionItems(sessionId, { included: true, search: search.trim() || undefined, afterItemId: append === 'selected' ? selectedNext || undefined : undefined, limit: 50 })
      ]);
      available = append === 'available' ? [...available, ...a.items] : a.items;
      selected = append === 'selected' ? [...selected, ...s.items] : s.items;
      availableNext = a.nextItemId; selectedNext = s.nextItemId; availableCount = a.totalCount; selectedCount = s.totalCount;
    } catch (err) { error = err instanceof Error ? err.message : 'Could not load scanned files.'; }
    finally { loading = false; }
  }
  function toggle(group: 'a' | 's', id: string, checked: boolean) {
    if (group === 'a') checkedAvailable = checked ? [...checkedAvailable, id] : checkedAvailable.filter((x) => x !== id);
    else checkedSelected = checked ? [...checkedSelected, id] : checkedSelected.filter((x) => x !== id);
  }
  async function move(action: 'include' | 'exclude') {
    const ids = action === 'include' ? checkedAvailable : checkedSelected;
    if (!ids.length) return;
    actionBusy = true; error = null;
    try { await bulkImportSessionItems(sessionId, { action, itemIds: ids }); checkedAvailable = []; checkedSelected = []; await load(); }
    catch (err) { error = err instanceof Error ? err.message : 'Could not update the selection.'; }
    finally { actionBusy = false; }
  }
  async function setDeleteSourceFiles(checked: boolean) {
    if (!session) return;
    const previous = session.deleteSourceFiles;
    session = { ...session, deleteSourceFiles: checked };
    try { const response = await updateImportSessionOptions(sessionId, { deleteSourceFiles: checked }); session = response.session ?? session; }
    catch (err) { session = session ? { ...session, deleteSourceFiles: previous } : session; error = err instanceof Error ? err.message : 'Could not update the session options.'; }
  }
</script>

<ImportWizardStepper current={2} {sessionId} />
<section class={card}>
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-white">File selection</h1><p class="mt-2 text-sm text-slate-400">Choose only the media files you want this session to import.</p></div>{#if session}<div class="ml-auto rounded-full bg-slate-900 px-3 py-1.5 text-xs font-semibold text-slate-300">{selectedCount} selected · {session.totalItems} found</div>{/if}</div>
  <div class="mt-5"><ImportNotice {error} /></div>
  {#if session?.status === 'scanning'}
    <div class="flex items-center justify-center gap-3 rounded-xl border border-slate-800 bg-slate-950/30 p-12 text-sm text-slate-400"><Spinner size="5" />Scanning the selected folder…</div>
  {:else}
    <div class="mb-4 flex gap-2"><Input bind:value={search} placeholder="Filter by filename or path" class="border-slate-800! bg-slate-950/60! text-slate-200!" /><Button color="dark" class="border-slate-700! bg-slate-900!" onclick={() => load()}>Search</Button></div>
    <div class="grid gap-5 xl:grid-cols-2">
      <div class="overflow-hidden rounded-xl border border-slate-800"><div class="flex items-center justify-between bg-slate-950/50 px-4 py-3"><h2 class="font-semibold text-slate-200">Available files <span class="text-slate-500">({availableCount})</span></h2><Button color="blue" class="border-0! text-xs!" disabled={!checkedAvailable.length || actionBusy} onclick={() => move('include')}>Add selected</Button></div><div class="max-h-[430px] divide-y divide-slate-800 overflow-y-auto">{#each available as item (item.itemId)}<label class="flex cursor-pointer items-center gap-3 px-4 py-3 hover:bg-slate-900/40"><Checkbox checked={checkedAvailable.includes(item.itemId)} onchange={(e) => toggle('a', item.itemId, e.currentTarget.checked)} /><span class="min-w-0"><span class="block truncate text-sm text-slate-200">{item.fileName}</span><span class="block truncate text-xs text-slate-500" title={item.relativePath}>{item.relativePath}</span></span><span class="ml-auto shrink-0 text-xs text-slate-600">{(item.fileSizeBytes / 1048576).toFixed(1)} MB</span></label>{:else}<p class="p-8 text-center text-sm text-slate-500">No available files.</p>{/each}</div>{#if availableNext}<Button color="dark" class="m-3 border-slate-700! bg-slate-900! text-xs!" onclick={() => load('available')}>Load more</Button>{/if}</div>
      <div class="overflow-hidden rounded-xl border border-slate-800"><div class="flex items-center justify-between bg-slate-950/50 px-4 py-3"><h2 class="font-semibold text-slate-200">Selected for import <span class="text-slate-500">({selectedCount})</span></h2><Button color="dark" class="border-red-900! bg-red-950/30! text-xs! text-red-200!" disabled={!checkedSelected.length || actionBusy} onclick={() => move('exclude')}>Remove selected</Button></div><div class="max-h-[430px] divide-y divide-slate-800 overflow-y-auto">{#each selected as item (item.itemId)}<label class="flex cursor-pointer items-center gap-3 px-4 py-3 hover:bg-slate-900/40"><Checkbox checked={checkedSelected.includes(item.itemId)} onchange={(e) => toggle('s', item.itemId, e.currentTarget.checked)} /><span class="min-w-0"><span class="block truncate text-sm text-slate-200">{item.fileName}</span><span class="block truncate text-xs text-slate-500" title={item.relativePath}>{item.relativePath}</span></span></label>{:else}<p class="p-8 text-center text-sm text-slate-500">Add at least one file to continue.</p>{/each}</div>{#if selectedNext}<Button color="dark" class="m-3 border-slate-700! bg-slate-900! text-xs!" onclick={() => load('selected')}>Load more</Button>{/if}</div>
    </div>
    <div class="mt-5 flex items-start gap-3 rounded-xl border border-slate-800 bg-slate-950/30 p-4">
      <Toggle checked={session?.deleteSourceFiles ?? false} onchange={(e) => setDeleteSourceFiles(e.currentTarget.checked)} />
      <div class="min-w-0">
        <p class="text-sm font-semibold text-slate-200">Delete source files after import</p>
        <p class="mt-1 text-xs text-slate-500">Each file (and its sidecars) is permanently removed from the incoming folder once it has imported successfully. Files that fail to import are kept.</p>
      </div>
    </div>
    <div class="mt-6 flex justify-between"><a class="rounded-lg px-4 py-2.5 text-sm font-semibold text-slate-400 hover:text-white" href="/admin/import/new/source">Back</a><a href={selectedCount ? `/admin/import/${sessionId}/metadata` : undefined} aria-disabled={!selectedCount} class={`rounded-lg bg-blue-600 px-6 py-2.5 text-sm font-semibold text-white hover:bg-blue-500 ${selectedCount ? '' : 'pointer-events-none opacity-40'}`}>Next: metadata</a></div>
  {/if}
</section>
