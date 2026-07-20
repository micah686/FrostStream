<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { Spinner } from 'flowbite-svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { getImportSession, listAllImportSessionItems, type ImportSession, type ImportSessionItem } from '$lib/api/imports';
  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const sessionId = $derived(page.params.sessionId ?? '');
  let session = $state<ImportSession | null>(null); let items = $state<ImportSessionItem[]>([]); let loading = $state(false); let error = $state<string | null>(null);
  onMount(() => { void load(); });
  async function load() { loading = true; error = null; try { [session, items] = await Promise.all([getImportSession(sessionId), listAllImportSessionItems(sessionId, { included: true })]); } catch (err) { error = err instanceof Error ? err.message : 'Could not load the review.'; } finally { loading = false; } }
  function label(item: ImportSessionItem) { if (item.metadataSource === 'ytDlp' || item.hasInfoJson) return 'yt-dlp'; if (item.metadataSource === 'manualMapping') return 'Manual mapping'; if (item.metadataSource === 'nfo') return 'NFO'; if (item.metadataSource === 'infoJson') return 'Info JSON'; return 'Placeholder'; }
  function pill(item: ImportSessionItem) { if (item.metadataSource === 'ytDlp' || item.hasInfoJson) return 'bg-emerald-950/60 text-emerald-300'; if (item.metadataSource === 'manualMapping') return 'bg-violet-950/60 text-violet-300'; if (item.metadataSource === 'nfo' || item.metadataSource === 'infoJson') return 'bg-blue-950/60 text-blue-300'; return 'bg-slate-900 text-slate-400'; }
  function pretty(json?: string | null) { if (!json) return 'No metadata details.'; try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; } }
</script>

<ImportWizardStepper current={5} {sessionId} />
<section class={card}>
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-white">Review import</h1><p class="mt-2 text-sm text-slate-400">Confirm every selected file and the metadata source FrostStream will use.</p></div>{#if session}<div class="ml-auto text-right text-xs text-slate-500"><p>{items.length} files</p><p class="mt-1 font-mono">{session.storageKey}</p></div>{/if}</div>
  <div class="mt-5"><ImportNotice {error} /></div>
  {#if loading}<div class="flex items-center gap-2 p-8 text-sm text-slate-400"><Spinner size="4" />Loading review…</div>{:else}<div class="space-y-2">{#each items as item (item.itemId)}<div class="rounded-xl border border-slate-800 bg-slate-950/25 px-4 py-3"><div class="flex items-center gap-3"><div class="min-w-0 flex-1"><p class="truncate text-sm font-medium text-slate-200">{item.title || item.fileName}</p><p class="truncate text-xs text-slate-500" title={item.relativePath}>{item.relativePath}</p></div><span class={`shrink-0 rounded-full px-2.5 py-1 text-xs font-semibold ${pill(item)}`}>{label(item)}</span></div>{#if item.metadataSource === 'manualMapping'}<details class="mt-3 border-t border-slate-800 pt-3"><summary class="cursor-pointer text-xs font-semibold text-slate-400">Show mapped metadata</summary><pre class="mt-3 max-h-72 overflow-auto whitespace-pre-wrap rounded-lg bg-black/25 p-3 text-xs text-slate-400">{pretty(item.metadataJson)}</pre></details>{/if}{#if item.metadataFetchState === 'failed'}<p class="mt-2 text-xs text-amber-400">yt-dlp failed; {label(item).toLowerCase()} metadata will be used instead.</p>{/if}</div>{:else}<p class="rounded-xl border border-dashed border-slate-800 p-8 text-center text-sm text-slate-500">No files selected.</p>{/each}</div>{/if}
  <div class="mt-6 flex justify-between"><a class="rounded-lg px-4 py-2.5 text-sm font-semibold text-slate-400 hover:text-white" href={`/admin/import/${sessionId}/mapping`}>Back</a><a class="rounded-lg bg-blue-600 px-6 py-2.5 text-sm font-semibold text-white hover:bg-blue-500" href={`/admin/import/${sessionId}/run`}>Next: import</a></div>
</section>
