<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { ArrowLeft } from '@lucide/svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { getImportSession, listAllImportSessionItems, type ImportSession, type ImportSessionItem } from '$lib/api/imports';
  const card = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
  const sessionId = $derived(page.params.sessionId ?? '');
  let session = $state<ImportSession | null>(null); let items = $state<ImportSessionItem[]>([]); let loading = $state(false); let error = $state<string | null>(null);
  onMount(() => { void load(); });
  async function load() { loading = true; error = null; try { [session, items] = await Promise.all([getImportSession(sessionId), listAllImportSessionItems(sessionId, { included: true })]); } catch (err) { error = err instanceof Error ? err.message : 'Could not load the review.'; } finally { loading = false; } }
  function label(item: ImportSessionItem) { if (item.metadataSource === 'ytDlp' || item.hasInfoJson) return 'info.json'; if (item.metadataSource === 'manualMapping') return 'Manual mapping'; if (item.metadataSource === 'nfo') return 'NFO'; if (item.metadataSource === 'infoJson') return 'Info JSON'; return 'Placeholder'; }
  function pill(item: ImportSessionItem) { if (item.metadataFetchState === 'failed') return 'badge-error'; if (item.metadataSource === 'ytDlp' || item.hasInfoJson) return 'badge-success'; return 'badge-neutral'; }
  function pretty(json?: string | null) { if (!json) return 'No metadata details.'; try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; } }
</script>

<section class={card}>
  <ImportWizardStepper current={5} {sessionId} />
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-base-content">Review import</h1><p class="mt-2 text-sm text-base-content/60">Confirm every selected file and the metadata source FrostStream will use.</p></div>{#if session}<div class="ml-auto text-right text-xs text-base-content/50"><p>{items.length} files</p><p class="mt-1 font-mono">{session.storageKey}</p></div>{/if}</div>
  <div class="mt-5"><ImportNotice {error} /></div>
  {#if session?.deleteSourceFiles}
    <div class="mb-5 flex items-start gap-3 rounded-xl border border-warning/30 bg-warning/10 p-4">
      <span class="text-lg leading-none text-warning">⚠</span>
      <div>
        <p class="text-sm font-semibold text-warning">Source files will be deleted</p>
        <p class="mt-1 text-xs text-warning">This session is set to permanently remove each source file (and its sidecars) from the incoming folder after it imports successfully. You can turn this off on the <a class="underline hover:text-warning" href={`/admin/import/${sessionId}/files`}>file selection</a> step.</p>
      </div>
    </div>
  {/if}
  {#if loading}<div class="flex items-center gap-2 p-8 text-sm text-base-content/60"><span class="loading loading-spinner loading-xs"></span>Loading review…</div>{:else}<div class="space-y-2">{#each items as item (item.itemId)}<div class="card border border-base-300 bg-base-100 p-4"><div class="flex items-center gap-3"><div class="min-w-0 flex-1"><p class="truncate text-sm font-medium text-base-content/90">{item.title || item.fileName}</p><p class="truncate text-xs text-base-content/50" title={item.relativePath}>{item.relativePath}</p></div><span class={`badge badge-sm shrink-0 ${pill(item)}`}>{label(item)}</span></div>{#if item.metadataSource === 'manualMapping'}<details class="mt-3 border-t border-base-300 pt-3"><summary class="cursor-pointer text-xs font-semibold text-base-content/60">Show mapped metadata</summary><pre class="mt-3 max-h-72 overflow-auto whitespace-pre-wrap rounded-lg bg-black/25 p-3 text-xs text-white/70">{pretty(item.metadataJson)}</pre></details>{/if}{#if item.metadataFetchState === 'failed'}<p class="mt-2 text-xs text-error">yt-dlp failed; {label(item).toLowerCase()} metadata will be used instead.</p>{/if}</div>{:else}<p class="rounded-xl border border-dashed border-base-300 p-8 text-center text-sm text-base-content/50">No files selected.</p>{/each}</div>{/if}
  <div class="mt-6 flex justify-between"><a class="btn btn-sm btn-neutral text-xs" href={`/admin/import/${sessionId}/mapping`}><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a><a class="btn btn-sm btn-primary text-xs" href={`/admin/import/${sessionId}/run`}>Next: import</a></div>
</section>
