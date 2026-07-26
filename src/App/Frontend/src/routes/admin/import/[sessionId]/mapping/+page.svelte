<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { applyImportSessionMapping, listAllImportSessionItems, mappingExampleUrl, mappingTemplateUrl, type ImportSessionItem } from '$lib/api/imports';
  const card = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
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
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-base-content">Manual mapping</h1><p class="mt-2 max-w-3xl text-sm text-base-content/60">Download a realistic example for reference, or generate a sparse mapping file for the selected files, edit it, then import it here.</p></div><div class="ml-auto flex flex-wrap gap-2"><a href={mappingExampleUrl(sessionId)} download><button class="btn btn-sm btn-neutral text-xs">Download example JSON</button></a><a href={mappingTemplateUrl(sessionId)} download><button class="btn btn-sm btn-neutral text-xs" disabled={!needsMapping.length}>Generate mapping file</button></a><input class="hidden" bind:this={fileInput} type="file" accept=".json,.csv,application/json,text/csv" onchange={upload} /><button class="btn btn-sm btn-primary text-xs" disabled={busy} onclick={() => fileInput?.click()}>{#if busy}<span class="loading loading-spinner loading-xs mr-2"></span>{/if}Import mapping</button></div></div>
  <div class="mt-5"><ImportNotice {error} {notice} /></div>
  <div class="grid gap-5 xl:grid-cols-2">
    <div class="overflow-hidden rounded-xl border border-base-300"><div class="bg-base-200/50 px-4 py-3"><h2 class="font-semibold text-base-content/90">Available for manual mapping <span class="text-base-content/50">({needsMapping.length})</span></h2></div><div class="max-h-[440px] divide-y divide-base-300 overflow-y-auto">{#each needsMapping as item (item.itemId)}<div class="px-4 py-3"><p class="truncate text-sm text-base-content/90">{item.fileName}</p><p class="truncate text-xs text-base-content/50" title={item.relativePath}>{item.relativePath}</p></div>{:else}<p class="p-8 text-center text-sm text-base-content/50">Every selected file already has yt-dlp or manual metadata.</p>{/each}</div></div>
    <div class="overflow-hidden rounded-xl border border-base-300"><div class="bg-base-200/50 px-4 py-3"><h2 class="font-semibold text-base-content/90">Metadata resolved <span class="text-base-content/50">({resolved.length})</span></h2></div><div class="max-h-[440px] divide-y divide-base-300 overflow-y-auto">{#each resolved as item (item.itemId)}<div class="bg-base-200/20 px-4 py-3 opacity-70"><div class="flex items-center gap-2"><p class="min-w-0 flex-1 truncate text-sm text-base-content/80">{item.fileName}</p><span class={`shrink-0 rounded-full px-2 py-1 text-[11px] font-semibold ${item.metadataSource === 'ytDlp' || item.hasInfoJson ? 'bg-success/10 text-success' : 'bg-secondary/10 text-secondary'}`}>{pill(item)}</span></div><p class="truncate text-xs text-base-content/40" title={item.relativePath}>{item.relativePath}</p></div>{:else}<p class="p-8 text-center text-sm text-base-content/50">No mappings applied yet.</p>{/each}</div></div>
  </div>
  <div class="mt-6 flex justify-between"><a class="rounded-lg px-4 py-2.5 text-sm font-semibold text-base-content/60 hover:text-base-content" href={`/admin/import/${sessionId}/metadata`}>Back</a><a class="rounded-lg bg-primary px-6 py-2.5 text-sm font-semibold text-primary-content hover:bg-primary" href={`/admin/import/${sessionId}/review`}>Next: review</a></div>
</section>
