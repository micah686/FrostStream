<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { Select } from '$lib/components/ui';
  import { ArrowLeft, FolderOpen } from '@lucide/svelte';
  import FolderPickerModal from '$lib/components/admin/FolderPickerModal.svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { createImportSession } from '$lib/api/imports';
  import { listStorage } from '$lib/api/storage';
  import { listWorkers, type WorkerInfo } from '$lib/api/workers';

  const card = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
  let storageKey = $state(''); let workerTag = $state(''); let subPath = $state('');
  let storageKeys = $state<string[]>([]); let workers = $state<WorkerInfo[]>([]); let pickerOpen = $state(false); let busy = $state(false); let error = $state<string | null>(null);
  const storageItems = $derived(storageKeys.map((key) => ({ value: key, name: key })));

  onMount(async () => {
    try { storageKeys = (await listStorage()).map((x) => x.key); storageKey = storageKeys.includes('default') ? 'default' : (storageKeys[0] ?? ''); workers = await listWorkers(); }
    catch { /* manual entry remains available */ }
  });

  const workerTags = $derived([...new Set(workers.flatMap((worker) => worker.tags))].sort());
  const visibleWorkers = $derived(workers.filter((worker) => !workerTag.trim() || worker.tags.some((tag) => tag.toLowerCase().includes(workerTag.trim().toLowerCase()))));

  async function next(event: SubmitEvent) {
    event.preventDefault();
    if (!storageKey.trim()) { error = 'Choose a destination storage target.'; return; }
    busy = true; error = null;
    try {
      const session = await createImportSession({ storageKey: storageKey.trim(), workerTag: workerTag.trim() || undefined, subPath: subPath.trim() || undefined });
      await goto(`/admin/import/${session.sessionId}/files`);
    } catch (err) { error = err instanceof Error ? err.message : 'Could not start the scan.'; }
    finally { busy = false; }
  }
</script>

<FolderPickerModal bind:open={pickerOpen} workerTag={workerTag.trim()} initialPath={subPath.trim()} onselect={(path) => (subPath = path)} />
<section class={card}>
  <ImportWizardStepper current={1} />
  <h1 class="text-xl font-bold text-base-content">Source selection</h1>
  <p class="mt-2 text-sm text-base-content/60">Choose the worker folder to scan and where imported media should be stored.</p>
  <div class="mt-5"><ImportNotice {error} /></div>
  <form class="grid gap-5 lg:grid-cols-2" onsubmit={next}>
    <div><label class="label mb-2" for="storage">Destination storage</label>{#if storageKeys.length}<Select id="storage" items={storageItems} bind:value={storageKey} />{:else}<input class="input w-full" id="storage" bind:value={storageKey} placeholder="default" />{/if}<span class="mt-1 block text-xs text-base-content/50">The storage key that receives imported files.</span></div>
    <div><label class="label mb-2" for="worker">Worker tag</label><input class="input w-full" id="worker" list="worker-tags" bind:value={workerTag} placeholder="Optional — search tags" /><datalist id="worker-tags">{#each workerTags as tag}<option value={tag}></option>{/each}</datalist><span class="mt-1 block text-xs text-base-content/50">Tags are reported by available workers. Leave blank when any worker can handle the import.</span></div>
    <div><span class="label mb-2 block">Available workers</span><div class="flex min-h-10 flex-wrap items-center gap-2">{#if visibleWorkers.length}{#each visibleWorkers as worker}<span class="badge badge-neutral">{worker.name}</span>{/each}{:else}<span class="text-xs text-base-content/60">No matching workers online</span>{/if}</div></div>
    <div class="lg:col-span-2"><label class="label mb-2" for="subpath">Source folder</label><div class="flex gap-2"><input class="input w-full min-w-0 flex-1" id="subpath" bind:value={subPath} placeholder="Select a folder on the chosen worker" /><button class="btn btn-sm btn-neutral" type="button" onclick={() => (pickerOpen = true)} disabled={!visibleWorkers.length}><FolderOpen class="mr-2 h-4 w-4" />Browse</button></div></div>
    <div class="flex justify-between gap-3 lg:col-span-2"><a href="/admin/import" class="btn btn-sm btn-neutral text-xs"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a><button class="btn btn-sm btn-primary text-xs" type="submit" disabled={busy}>{#if busy}<span class="loading loading-spinner loading-xs mr-2"></span>{/if}Scan files and continue</button></div>
  </form>
</section>
