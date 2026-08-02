<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { ArrowLeft } from '@lucide/svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { bulkImportSessionItems, getImportSession, listImportSessionItems, updateImportSessionOptions, type ImportSession, type ImportSessionItem } from '$lib/api/imports';

  const card = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
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

<section class={card}>
  <ImportWizardStepper current={2} {sessionId} />
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-base-content">File selection</h1><p class="mt-2 text-sm text-base-content/60">Choose only the media files you want this session to import.</p></div></div>
  <div class="mt-5"><ImportNotice {error} /></div>
  {#if session?.status === 'scanning'}
    <div class="flex items-center justify-center gap-3 rounded-xl border border-base-300 bg-base-200/30 p-12 text-sm text-base-content/60"><span class="loading loading-spinner loading-sm"></span>Scanning the selected folder…</div>
  {:else}
    <div class="mb-4 flex items-end gap-2"><input class="input w-full" bind:value={search} placeholder="Filter by filename or path" /><button class="btn btn-sm btn-neutral mb-0.5" onclick={() => load()}>Search</button></div>
    <div class="grid gap-5 xl:grid-cols-2">
      <div class="overflow-hidden rounded-xl border border-base-300"><div class="flex items-center justify-between bg-base-200/50 px-4 py-3"><h2 class="font-semibold text-base-content/90">Available files <span class="text-base-content/50">({availableCount})</span></h2><button class="btn btn-sm btn-primary text-xs" disabled={!checkedAvailable.length || actionBusy} onclick={() => move('include')}>Add selected</button></div><div class="max-h-[430px] divide-y divide-base-300 overflow-y-auto">{#each available as item (item.itemId)}<label class="flex cursor-pointer items-center gap-3 px-4 py-3 hover:bg-base-200/40"><input type="checkbox" class="checkbox" checked={checkedAvailable.includes(item.itemId)} onchange={(e) => toggle('a', item.itemId, e.currentTarget.checked)} /><span class="min-w-0"><span class="block truncate text-sm text-base-content/90">{item.fileName}</span><span class="block truncate text-xs text-base-content/50" title={item.relativePath}>{item.relativePath}</span></span><span class="ml-auto shrink-0 text-xs text-base-content/40">{(item.fileSizeBytes / 1048576).toFixed(1)} MB</span></label>{:else}<p class="p-8 text-center text-sm text-base-content/50">No available files.</p>{/each}</div>{#if availableNext}<button class="btn btn-sm btn-neutral m-3 text-xs" onclick={() => load('available')}>Load more</button>{/if}</div>
      <div class="overflow-hidden rounded-xl border border-base-300"><div class="flex items-center justify-between bg-base-200/50 px-4 py-3"><h2 class="font-semibold text-base-content/90">Selected for import <span class="text-base-content/50">({selectedCount})</span></h2><button class="btn btn-sm btn-neutral text-xs" disabled={!checkedSelected.length || actionBusy} onclick={() => move('exclude')}>Remove selected</button></div><div class="max-h-[430px] divide-y divide-base-300 overflow-y-auto">{#each selected as item (item.itemId)}<label class="flex cursor-pointer items-center gap-3 px-4 py-3 hover:bg-base-200/40"><input type="checkbox" class="checkbox" checked={checkedSelected.includes(item.itemId)} onchange={(e) => toggle('s', item.itemId, e.currentTarget.checked)} /><span class="min-w-0"><span class="block truncate text-sm text-base-content/90">{item.fileName}</span><span class="block truncate text-xs text-base-content/50" title={item.relativePath}>{item.relativePath}</span></span></label>{:else}<p class="p-8 text-center text-sm text-base-content/50">Add at least one file to continue.</p>{/each}</div>{#if selectedNext}<button class="btn btn-sm btn-neutral m-3 text-xs" onclick={() => load('selected')}>Load more</button>{/if}</div>
    </div>
    <div class="card mt-5 flex items-start gap-3 border border-base-300 bg-base-100 p-4">
      <input type="checkbox" class="toggle toggle-primary" checked={session?.deleteSourceFiles ?? false} onchange={(e) => setDeleteSourceFiles(e.currentTarget.checked)} />
      <div class="min-w-0">
        <p class="text-sm font-semibold text-base-content/90">Delete source files after import</p>
        <p class="mt-1 text-xs text-base-content/50">Each file (and its sidecars) is permanently removed from the incoming folder once it has imported successfully. Files that fail to import are kept.</p>
      </div>
    </div>
    <div class="mt-6 flex justify-between"><a class="btn btn-sm btn-neutral text-xs" href="/admin/import/new/source"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a><a href={selectedCount ? `/admin/import/${sessionId}/metadata` : undefined} aria-disabled={!selectedCount} class={`btn btn-sm btn-primary text-xs ${selectedCount ? '' : 'pointer-events-none opacity-40'}`}>Next: metadata</a></div>
  {/if}
</section>
