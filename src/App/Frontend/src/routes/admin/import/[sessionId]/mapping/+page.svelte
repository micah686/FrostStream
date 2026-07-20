<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { Button, Spinner } from 'flowbite-svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { applyImportSessionMapping, listAllImportSessionItems, mappingTemplateUrl, type ImportSessionItem } from '$lib/api/imports';
  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const sessionId = $derived(page.params.sessionId ?? '');
  let items = $state<ImportSessionItem[]>([]); let loading = $state(false); let busy = $state(false); let error = $state<string | null>(null); let notice = $state<string | null>(null); let fileInput: HTMLInputElement;
  const needsMapping = $derived(items.filter((x) => x.metadataSource !== 'ytDlp' && x.metadataSource !== 'manualMapping' && !x.hasInfoJson));
  const resolved = $derived(items.filter((x) => x.metadataSource === 'ytDlp' || x.metadataSource === 'manualMapping' || x.hasInfoJson));
  onMount(() => { void load(); });
  async function load() { loading = true; error = null; try { items = await listAllImportSessionItems(sessionId, { included: true }); } catch (err) { error = err instanceof Error ? err.message : 'Could not load mapping items.'; } finally { loading = false; } }
  async function upload(event: Event) { const input = event.currentTarget as HTMLInputElement; const file = input.files?.[0]; if (!file) return; busy = true; error = null; notice = null; try { const response = await applyImportSessionMapping(sessionId, file); notice = `Manual mapping applied to ${response.matchedCount} file${response.matchedCount === 1 ? '' : 's'}${response.unmatchedCount ? `; ${response.unmatchedCount} unmatched` : ''}.`; await load(); } catch (err) { error = err instanceof Error ? err.message : 'Could not import the mapping file.'; } finally { busy = false; input.value = ''; } }
  function pill(item: ImportSessionItem) { return item.metadataSource === 'ytDlp' || item.hasInfoJson ? 'yt-dlp metadata found' : 'manual mapping'; }
</script>

<ImportWizardStepper current={4} {sessionId} />
<section class={card}>
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-white">Manual mapping</h1><p class="mt-2 max-w-3xl text-sm text-slate-400">Download a JSON template for files that still need metadata, edit it, then import it here. This step is optional.</p></div><div class="ml-auto flex flex-wrap gap-2"><a href={mappingTemplateUrl(sessionId)} download><Button color="dark" class="border-slate-700! bg-slate-900! text-xs!" disabled={!needsMapping.length}>Generate mapping template</Button></a><input class="hidden" bind:this={fileInput} type="file" accept=".json,.csv,application/json,text/csv" onchange={upload} /><Button color="blue" class="border-0! text-xs!" disabled={busy} onclick={() => fileInput?.click()}>{#if busy}<Spinner size="4" class="mr-2" />{/if}Import mapping</Button></div></div>
  <div class="mt-5"><ImportNotice {error} {notice} /></div>
  <div class="grid gap-5 xl:grid-cols-2">
    <div class="overflow-hidden rounded-xl border border-slate-800"><div class="bg-slate-950/50 px-4 py-3"><h2 class="font-semibold text-slate-200">Available for manual mapping <span class="text-slate-500">({needsMapping.length})</span></h2></div><div class="max-h-[440px] divide-y divide-slate-800 overflow-y-auto">{#each needsMapping as item (item.itemId)}<div class="px-4 py-3"><p class="truncate text-sm text-slate-200">{item.fileName}</p><p class="truncate text-xs text-slate-500" title={item.relativePath}>{item.relativePath}</p></div>{:else}<p class="p-8 text-center text-sm text-slate-500">Every selected file already has yt-dlp or manual metadata.</p>{/each}</div></div>
    <div class="overflow-hidden rounded-xl border border-slate-800"><div class="bg-slate-950/50 px-4 py-3"><h2 class="font-semibold text-slate-200">Metadata resolved <span class="text-slate-500">({resolved.length})</span></h2></div><div class="max-h-[440px] divide-y divide-slate-800 overflow-y-auto">{#each resolved as item (item.itemId)}<div class="bg-slate-950/20 px-4 py-3 opacity-70"><div class="flex items-center gap-2"><p class="min-w-0 flex-1 truncate text-sm text-slate-300">{item.fileName}</p><span class={`shrink-0 rounded-full px-2 py-1 text-[11px] font-semibold ${item.metadataSource === 'ytDlp' || item.hasInfoJson ? 'bg-emerald-950/60 text-emerald-300' : 'bg-violet-950/60 text-violet-300'}`}>{pill(item)}</span></div><p class="truncate text-xs text-slate-600" title={item.relativePath}>{item.relativePath}</p></div>{:else}<p class="p-8 text-center text-sm text-slate-500">No mappings applied yet.</p>{/each}</div></div>
  </div>
  <div class="mt-6 flex justify-between"><a class="rounded-lg px-4 py-2.5 text-sm font-semibold text-slate-400 hover:text-white" href={`/admin/import/${sessionId}/metadata`}>Back</a><a class="rounded-lg bg-blue-600 px-6 py-2.5 text-sm font-semibold text-white hover:bg-blue-500" href={`/admin/import/${sessionId}/review`}>Next: review</a></div>
</section>
