<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Spinner } from 'flowbite-svelte';
  import { FileImportOutline, RefreshOutline } from 'flowbite-svelte-icons';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import { listImportSessions, type ImportSession } from '$lib/api/imports';

  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  let sessions = $state<ImportSession[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  onMount(() => { void load(); });

  async function load() {
    loading = true; error = null;
    try { sessions = (await listImportSessions()).items; }
    catch (err) { error = err instanceof Error ? err.message : 'Could not load import sessions.'; }
    finally { loading = false; }
  }

  function resumePath(session: ImportSession) {
    if (session.status === 'committing' || session.status === 'completed' || session.status === 'completedWithFailures') return 'run';
    return session.status === 'scanning' ? 'files' : 'files';
  }
</script>

<section class={card}>
  <ImportNotice {error} />
  <div class="flex flex-col items-start gap-5 sm:flex-row sm:items-center">
    <div>
      <h1 class="text-xl font-bold text-white">Import local media</h1>
      <p class="mt-2 max-w-2xl text-sm leading-6 text-slate-400">Choose files from a worker’s incoming folder, attach the best available metadata, review everything, and monitor the import.</p>
    </div>
    <a href="/admin/import/new/source" class="sm:ml-auto">
      <Button color="blue" size="lg" class="border-0! px-6! font-semibold!"><FileImportOutline class="mr-2 h-5 w-5" />Start import</Button>
    </a>
  </div>
</section>

<section class={card}>
  <div class="flex items-center justify-between gap-3">
    <div><h2 class="text-base font-bold text-slate-100">Import sessions</h2><p class="mt-1 text-sm text-slate-500">Resume an unfinished import or inspect its result.</p></div>
    <Button color="dark" class="border-slate-700! bg-slate-900! text-xs!" onclick={load} disabled={loading}><RefreshOutline class="mr-1.5 h-4 w-4" />Refresh</Button>
  </div>
  {#if loading && !sessions.length}
    <div class="mt-6 flex items-center gap-2 text-sm text-slate-400"><Spinner size="4" />Loading sessions</div>
  {:else if !sessions.length}
    <div class="mt-6 rounded-xl border border-dashed border-slate-800 p-10 text-center text-sm text-slate-500">No import sessions yet.</div>
  {:else}
    <div class="mt-5 overflow-x-auto rounded-xl border border-slate-800">
      <table class="min-w-full text-left text-sm">
        <thead class="bg-slate-950/60 text-xs uppercase tracking-wide text-slate-500"><tr><th class="px-4 py-3">Source</th><th class="px-4 py-3">Status</th><th class="px-4 py-3">Selected</th><th class="px-4 py-3">Imported</th><th class="px-4 py-3"></th></tr></thead>
        <tbody class="divide-y divide-slate-800">
          {#each sessions as session (session.sessionId)}
            <tr class="hover:bg-slate-900/40">
              <td class="max-w-72 px-4 py-3"><p class="truncate text-slate-200" title={session.subPath || 'incoming/'}>{session.subPath || 'incoming/'}</p><p class="mt-1 font-mono text-xs text-slate-600">{session.storageKey}</p></td>
              <td class="px-4 py-3 text-slate-300">{session.status}</td>
              <td class="px-4 py-3 text-slate-300">{session.totalItems - session.excludedItems} / {session.totalItems}</td>
              <td class="px-4 py-3 text-slate-300">{session.importedItems + session.alreadyImportedItems}</td>
              <td class="px-4 py-3 text-right"><a class="text-xs font-semibold text-blue-300 hover:text-blue-200" href={`/admin/import/${session.sessionId}/${resumePath(session)}`}>Open →</a></td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</section>
