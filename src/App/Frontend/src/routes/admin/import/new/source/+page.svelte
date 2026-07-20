<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { Button, Helper, Input, Label, Select, Spinner } from 'flowbite-svelte';
  import { FolderOpenOutline } from 'flowbite-svelte-icons';
  import FolderPickerModal from '$lib/components/admin/FolderPickerModal.svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { createImportSession } from '$lib/api/imports';
  import { listStorage } from '$lib/api/storage';

  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const field = 'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  let storageKey = $state(''); let workerTag = $state(''); let subPath = $state(''); let note = $state('');
  let storageKeys = $state<string[]>([]); let pickerOpen = $state(false); let busy = $state(false); let error = $state<string | null>(null);
  const storageItems = $derived(storageKeys.map((key) => ({ value: key, name: key })));

  onMount(async () => {
    try { storageKeys = (await listStorage()).map((x) => x.key); storageKey = storageKeys.includes('default') ? 'default' : (storageKeys[0] ?? ''); }
    catch { /* manual entry remains available */ }
  });

  async function next(event: SubmitEvent) {
    event.preventDefault();
    if (!storageKey.trim()) { error = 'Choose a destination storage target.'; return; }
    busy = true; error = null;
    try {
      const session = await createImportSession({ storageKey: storageKey.trim(), workerTag: workerTag.trim() || undefined, subPath: subPath.trim() || undefined, requestedBy: note.trim() || undefined });
      await goto(`/admin/import/${session.sessionId}/files`);
    } catch (err) { error = err instanceof Error ? err.message : 'Could not start the scan.'; }
    finally { busy = false; }
  }
</script>

<ImportWizardStepper current={1} />
<FolderPickerModal bind:open={pickerOpen} workerTag={workerTag.trim()} initialPath={subPath.trim()} onselect={(path) => (subPath = path)} />
<section class={card}>
  <h1 class="text-xl font-bold text-white">Source selection</h1>
  <p class="mt-2 text-sm text-slate-400">Choose the worker folder to scan and where imported media should be stored.</p>
  <div class="mt-5"><ImportNotice {error} /></div>
  <form class="grid gap-5 lg:grid-cols-2" onsubmit={next}>
    <div><Label for="storage" class="mb-2 text-slate-300">Destination storage</Label>{#if storageKeys.length}<Select id="storage" items={storageItems} bind:value={storageKey} class={field} />{:else}<Input id="storage" bind:value={storageKey} placeholder="default" class={field} />{/if}<Helper class="mt-1 text-xs! text-slate-500!">The storage key that receives imported files.</Helper></div>
    <div><Label for="worker" class="mb-2 text-slate-300">Worker tag</Label><Input id="worker" bind:value={workerTag} placeholder="Optional, for example nas" class={field} /><Helper class="mt-1 text-xs! text-slate-500!">Use a tag when the incoming folder exists on a specific worker.</Helper></div>
    <div class="lg:col-span-2"><Label for="subpath" class="mb-2 text-slate-300">Incoming folder</Label><div class="flex gap-2"><Input id="subpath" bind:value={subPath} placeholder="Root incoming folder" class="{field} min-w-0 flex-1" /><Button type="button" color="dark" class="border-slate-700! bg-slate-900!" onclick={() => (pickerOpen = true)}><FolderOpenOutline class="mr-2 h-4 w-4" />Browse</Button></div></div>
    <div class="lg:col-span-2"><Label for="note" class="mb-2 text-slate-300">Note <span class="text-slate-600">(optional)</span></Label><Input id="note" bind:value={note} placeholder="2019 channel backfill" class={field} /></div>
    <div class="flex justify-between gap-3 lg:col-span-2"><a href="/admin/import" class="rounded-lg px-4 py-2.5 text-sm font-semibold text-slate-400 hover:text-white">Cancel</a><Button type="submit" color="blue" class="border-0! px-6! font-semibold!" disabled={busy}>{#if busy}<Spinner size="4" class="mr-2" />{/if}Scan files and continue</Button></div>
  </form>
</section>
