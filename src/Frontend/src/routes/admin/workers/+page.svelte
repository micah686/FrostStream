<script lang="ts">
  import { onMount } from 'svelte';
  import { RefreshCw } from '@lucide/svelte';
  import { listWorkers, setWorkerCapacity, type WorkerInfo } from '#lib/api/workers.js';

  let workers = $state<WorkerInfo[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let capacityDraft = $state<Record<string, number>>({});
  let savingWorkerId = $state<string | null>(null);

  async function load() {
    loading = true; error = null;
    try {
      workers = await listWorkers();
      capacityDraft = Object.fromEntries(workers.map(worker => [worker.workerId, worker.maxActiveJobs ?? 2]));
    }
    catch (e) { error = e instanceof Error ? e.message : 'Could not load workers.'; }
    finally { loading = false; }
  }
  onMount(() => { void load(); });

  function online(worker: WorkerInfo) {
    const age = Date.now() - Date.parse(worker.lastOnline);
    return age <= 45_000;
  }
  async function saveCapacity(worker: WorkerInfo) {
    const value = Number(capacityDraft[worker.workerId]);
    if (!Number.isInteger(value) || value < 1 || value > 32) {
      error = 'Maximum active jobs must be a whole number from 1 to 32.';
      return;
    }
    savingWorkerId = worker.workerId;
    error = null;
    try { await setWorkerCapacity(worker.workerId, value); await load(); }
    catch (e) { error = e instanceof Error ? e.message : 'Could not update worker capacity.'; }
    finally { savingWorkerId = null; }
  }
</script>

<section class="card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6">
  <div class="flex items-center justify-between gap-3">
    <div><h1 class="text-xl font-bold text-base-content">Workers</h1><p class="mt-2 text-sm text-base-content/60">Set the maximum active download jobs per worker. Extra jobs wait before metadata is fetched.</p></div>
    <button class="btn btn-sm btn-neutral text-xs" onclick={load} disabled={loading}><RefreshCw class="mr-1.5 h-4 w-4" />Refresh</button>
  </div>
  {#if error}<p class="alert alert-error mt-5 text-sm" role="alert">{error}</p>{/if}
  {#if loading && !workers.length}<div class="mt-6 flex items-center gap-2 text-sm text-base-content/60"><span class="loading loading-spinner loading-xs"></span>Loading workers</div>
  {:else if !workers.length}<div class="mt-6 rounded-box border-[length:var(--border)] border-dashed border-base-300 p-10 text-center text-sm text-base-content/50">No workers have reported yet.</div>
  {:else}<div class="mt-5 overflow-x-auto rounded-box border-[length:var(--border)] border-base-300"><table class="min-w-full text-left text-sm"><thead class="bg-base-200/60 text-xs uppercase tracking-wide text-base-content/50"><tr><th class="px-4 py-3">Name</th><th class="px-4 py-3">Tags</th><th class="px-4 py-3">Active jobs</th><th class="px-4 py-3">Maximum active jobs</th><th class="px-4 py-3">Last online</th><th class="px-4 py-3">Incoming root</th></tr></thead><tbody class="divide-y divide-base-300">{#each workers as worker (worker.workerId)}<tr><td class="px-4 py-3 text-base-content/90"><span class={["mr-2 inline-block h-2 w-2 rounded-full", online(worker) ? "bg-success" : "bg-base-300"]}></span>{worker.name}</td><td class="px-4 py-3 text-base-content/80">{worker.tags.length ? worker.tags.join(', ') : '—'}</td><td class="px-4 py-3 text-base-content/80">{worker.activeJobs ?? 0}</td><td class="px-4 py-3"><div class="flex items-center gap-2"><input class="input input-bordered input-sm w-20" type="number" min="1" max="32" step="1" aria-label={`Maximum active jobs for ${worker.name}`} bind:value={capacityDraft[worker.workerId]} /><button class="btn btn-sm btn-primary" onclick={() => saveCapacity(worker)} disabled={savingWorkerId === worker.workerId || capacityDraft[worker.workerId] === worker.maxActiveJobs}>{savingWorkerId === worker.workerId ? 'Saving…' : 'Save'}</button></div></td><td class="px-4 py-3 text-base-content/80">{new Date(worker.lastOnline).toLocaleString()}</td><td class="px-4 py-3 font-mono text-xs text-base-content/50">{worker.incomingRoot}</td></tr>{/each}</tbody></table></div>{/if}
</section>

