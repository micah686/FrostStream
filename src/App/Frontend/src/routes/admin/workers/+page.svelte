<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Spinner } from 'flowbite-svelte';
  import { RefreshOutline } from 'flowbite-svelte-icons';
  import { listWorkers, type WorkerInfo } from '$lib/api/workers';

  let workers = $state<WorkerInfo[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);

  async function load() {
    loading = true; error = null;
    try { workers = await listWorkers(); }
    catch (e) { error = e instanceof Error ? e.message : 'Could not load workers.'; }
    finally { loading = false; }
  }
  onMount(() => { void load(); });

  function online(worker: WorkerInfo) {
    const age = Date.now() - Date.parse(worker.lastOnline);
    return age <= 45_000;
  }
</script>

<section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
  <div class="flex items-center justify-between gap-3">
    <div><h1 class="text-xl font-bold text-white">Workers</h1><p class="mt-2 text-sm text-slate-400">Workers report their name, routing tags, incoming folder, and heartbeat every 15 seconds.</p></div>
    <Button color="dark" class="border-slate-700! bg-slate-900! text-xs!" onclick={load} disabled={loading}><RefreshOutline class="mr-1.5 h-4 w-4" />Refresh</Button>
  </div>
  {#if error}<p class="mt-5 rounded-lg border border-red-900/60 bg-red-950/30 p-3 text-sm text-red-300">{error}</p>{/if}
  {#if loading && !workers.length}<div class="mt-6 flex items-center gap-2 text-sm text-slate-400"><Spinner size="4" />Loading workers</div>
  {:else if !workers.length}<div class="mt-6 rounded-xl border border-dashed border-slate-800 p-10 text-center text-sm text-slate-500">No workers have reported yet.</div>
  {:else}<div class="mt-5 overflow-x-auto rounded-xl border border-slate-800"><table class="min-w-full text-left text-sm"><thead class="bg-slate-950/60 text-xs uppercase tracking-wide text-slate-500"><tr><th class="px-4 py-3">Name</th><th class="px-4 py-3">Tags</th><th class="px-4 py-3">Last online</th><th class="px-4 py-3">Incoming root</th></tr></thead><tbody class="divide-y divide-slate-800">{#each workers as worker (worker.workerId)}<tr><td class="px-4 py-3 text-slate-200"><span class:online={online(worker)} class="mr-2 inline-block h-2 w-2 rounded-full bg-slate-600"></span>{worker.name}</td><td class="px-4 py-3 text-slate-300">{worker.tags.length ? worker.tags.join(', ') : '—'}</td><td class="px-4 py-3 text-slate-300">{new Date(worker.lastOnline).toLocaleString()}</td><td class="px-4 py-3 font-mono text-xs text-slate-500">{worker.incomingRoot}</td></tr>{/each}</tbody></table></div>{/if}
</section>

<style>.online { background-color: rgb(52 211 153); }</style>
