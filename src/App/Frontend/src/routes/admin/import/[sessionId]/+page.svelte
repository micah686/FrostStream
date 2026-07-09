<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { Button, Input, Select, Spinner } from 'flowbite-svelte';
  import { ArrowLeftOutline, ExclamationCircleOutline, RefreshOutline } from 'flowbite-svelte-icons';
  import {
    applyImportSessionMapping,
    bulkImportSessionItems,
    cancelImportSession,
    commitImportSession,
    enrichImportSession,
    getImportSession,
    listImportSessionItems,
    patchImportSessionItem,
    retryFailedImportSession,
    type ImportSession,
    type ImportSessionItem,
    type ImportSessionItemMetadataState
  } from '$lib/api/imports';

  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const metadataFilters = [
    { value: '', name: 'All metadata states' },
    { value: 'incomplete', name: 'Incomplete' },
    { value: 'ready', name: 'Ready' },
    { value: 'edited', name: 'Edited' },
    { value: 'placeholderAccepted', name: 'Placeholder accepted' }
  ];

  let session = $state<ImportSession | null>(null);
  let items = $state<ImportSessionItem[]>([]);
  let nextItemId = $state<string | null | undefined>(null);
  let totalCount = $state(0);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let notice = $state<string | null>(null);
  let search = $state('');
  let metadataState = $state('');
  let selectedIds = $state<string[]>([]);
  let editItem = $state<ImportSessionItem | null>(null);
  let editTitle = $state('');
  let editProvider = $state('');
  let editSourceMediaId = $state('');
  let editSourceUrl = $state('');
  let actionBusy = $state(false);

  const sessionId = $derived(page.params.sessionId);

  onMount(() => {
    void load();
  });

  async function load(afterItemId?: string) {
    if (!sessionId) {
      error = 'Import session id is missing from the route.';
      return;
    }

    loading = true;
    error = null;
    try {
      const [sessionResult, itemResult] = await Promise.all([
        getImportSession(sessionId),
        listImportSessionItems(sessionId, {
          search: search.trim() || undefined,
          metadataState: (metadataState || undefined) as ImportSessionItemMetadataState | undefined,
          afterItemId,
          limit: 100
        })
      ]);
      session = sessionResult;
      items = afterItemId ? [...items, ...itemResult.items] : itemResult.items;
      nextItemId = itemResult.nextItemId;
      totalCount = itemResult.totalCount;
      if (!afterItemId) selectedIds = [];
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load import session.';
    } finally {
      loading = false;
    }
  }

  function formatBytes(value: number) {
    if (value < 1024) return `${value} B`;
    const units = ['KB', 'MB', 'GB', 'TB'];
    let size = value / 1024;
    let unit = 0;
    while (size >= 1024 && unit < units.length - 1) {
      size /= 1024;
      unit++;
    }
    return `${size.toFixed(size >= 10 ? 0 : 1)} ${units[unit]}`;
  }

  function toggleSelected(id: string, checked: boolean) {
    selectedIds = checked ? [...selectedIds, id] : selectedIds.filter((value) => value !== id);
  }

  function startEdit(item: ImportSessionItem) {
    editItem = item;
    editTitle = item.title ?? '';
    editProvider = item.provider ?? '';
    editSourceMediaId = item.sourceMediaId ?? '';
    editSourceUrl = item.sourceUrl ?? '';
  }

  async function saveEdit() {
    if (!sessionId || !editItem) return;
    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await patchImportSessionItem(sessionId, editItem.itemId, {
        title: editTitle.trim() || undefined,
        provider: editProvider.trim() || undefined,
        sourceMediaId: editSourceMediaId.trim() || undefined,
        sourceUrl: editSourceUrl.trim() || undefined
      });
      if (response.item) items = items.map((item) => (item.itemId === response.item?.itemId ? response.item : item));
      if (response.session) session = response.session;
      editItem = null;
      notice = 'Item metadata saved.';
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not save item metadata.';
    } finally {
      actionBusy = false;
    }
  }

  async function bulk(action: 'acceptPlaceholders' | 'exclude' | 'include' | 'resetFailed', selectedOnly = false) {
    if (!sessionId) return;
    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await bulkImportSessionItems(sessionId, {
        action,
        itemIds: selectedOnly ? selectedIds : undefined,
        metadataState: selectedOnly ? undefined : ((metadataState || undefined) as ImportSessionItemMetadataState | undefined),
        search: selectedOnly ? undefined : search.trim() || undefined
      });
      if (response.session) session = response.session;
      notice = `${response.affectedCount} item${response.affectedCount === 1 ? '' : 's'} updated.`;
      await load();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not apply bulk action.';
    } finally {
      actionBusy = false;
    }
  }

  async function uploadMapping(event: Event) {
    if (!sessionId) return;
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await applyImportSessionMapping(sessionId, file);
      if (response.session) session = response.session;
      notice = `Mapping applied: ${response.matchedCount} matched, ${response.unmatchedCount} unmatched.`;
      await load();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not apply mapping file.';
    } finally {
      actionBusy = false;
      input.value = '';
    }
  }

  async function enrich(selectedOnly = false) {
    if (!sessionId) return;
    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await enrichImportSession(sessionId, selectedOnly ? selectedIds : undefined);
      if (response.session) session = response.session;
      notice =
        response.queuedCount === 0
          ? 'No items eligible for enrichment (needs a source URL and no prior enrichment).'
          : `${response.queuedCount} item${response.queuedCount === 1 ? '' : 's'} queued for yt-dlp enrichment.`;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not queue enrichment.';
    } finally {
      actionBusy = false;
    }
  }

  async function commitSession() {
    if (!sessionId) return;
    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await commitImportSession(sessionId);
      if (response.session) session = response.session;
      notice = `${response.approvedCount} item${response.approvedCount === 1 ? '' : 's'} approved for commit.`;
      await load();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not commit import session.';
    } finally {
      actionBusy = false;
    }
  }

  async function retryFailed() {
    if (!sessionId) return;
    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await retryFailedImportSession(sessionId);
      if (response.session) session = response.session;
      notice = `${response.resetCount} failed item${response.resetCount === 1 ? '' : 's'} queued for retry.`;
      await load();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not retry failed items.';
    } finally {
      actionBusy = false;
    }
  }

  async function cancelSession() {
    if (!sessionId) return;
    actionBusy = true;
    error = null;
    notice = null;
    try {
      const response = await cancelImportSession(sessionId);
      if (response.session) session = response.session;
      notice = 'Import session cancelled.';
      await load();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not cancel import session.';
    } finally {
      actionBusy = false;
    }
  }
</script>

<section class={cardClass}>
  <div class="flex flex-wrap items-start gap-3">
    <div>
      <a href="/admin/import" class="inline-flex items-center gap-1 text-xs font-semibold text-slate-500 hover:text-slate-300">
        <ArrowLeftOutline class="h-3.5 w-3.5" />
        Import sessions
      </a>
      <h1 class="mt-3 font-mono text-sm font-bold text-slate-100">{sessionId}</h1>
      {#if session}
        <p class="mt-2 text-sm text-slate-400">
          {session.status} · {session.totalItems} discovered · {session.readyItems} ready · {session.incompleteItems} incomplete
        </p>
      {/if}
    </div>
    <div class="ml-auto flex flex-wrap justify-end gap-2">
      <Button color="blue" class="border-0! px-3! py-2! text-xs!" onclick={commitSession} disabled={actionBusy || loading}>
        Commit
      </Button>
      <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={retryFailed} disabled={actionBusy || loading || !(session?.failedItems ?? 0)}>
        Retry failed
      </Button>
      <Button color="dark" class="border-red-900! bg-red-950/40! px-3! py-2! text-xs! text-red-200!" onclick={cancelSession} disabled={actionBusy || loading}>
        Cancel
      </Button>
      <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={() => load()} disabled={loading}>
        <RefreshOutline class="mr-1.5 h-4 w-4" />
        Refresh
      </Button>
    </div>
  </div>

  {#if error}
    <div class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error}</span>
    </div>
  {/if}
  {#if notice}
    <div class="mt-5 rounded-xl border border-emerald-900/60 bg-emerald-950/25 p-3 text-sm text-emerald-300">
      {notice}
    </div>
  {/if}
</section>

<section class={cardClass}>
  <div class="grid gap-3 md:grid-cols-[1fr_220px_auto]">
    <Input bind:value={search} placeholder="Search filename or title" class={inputClass} />
    <Select items={metadataFilters} bind:value={metadataState} class={inputClass} />
    <Button color="blue" class="border-0! px-4! py-2.5! text-xs! font-semibold!" onclick={() => load()} disabled={loading}>
      {#if loading}<Spinner size="4" class="mr-1.5" />{/if}
      Apply
    </Button>
  </div>

  <div class="mt-4 flex flex-wrap items-center gap-2">
    <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={() => bulk('acceptPlaceholders')} disabled={actionBusy}>
      Accept filtered placeholders
    </Button>
    <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={() => bulk('exclude', true)} disabled={actionBusy || selectedIds.length === 0}>
      Exclude selected
    </Button>
    <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={() => bulk('include', true)} disabled={actionBusy || selectedIds.length === 0}>
      Include selected
    </Button>
    <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={() => bulk('resetFailed')} disabled={actionBusy}>
      Reset filtered failed
    </Button>
    <Button
      color="dark"
      class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!"
      onclick={() => enrich(selectedIds.length > 0)}
      disabled={actionBusy || session?.status !== 'reviewing'}
      title="Re-fetch metadata with yt-dlp for items that have a source URL"
    >
      Enrich {selectedIds.length > 0 ? 'selected' : 'all'} (yt-dlp)
    </Button>
    <label class="ml-auto inline-flex cursor-pointer items-center rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-xs font-semibold text-slate-200 hover:bg-slate-800">
      Apply mapping
      <input class="sr-only" type="file" accept=".csv,.json,text/csv,application/json" onchange={uploadMapping} disabled={actionBusy} />
    </label>
  </div>

  {#if editItem}
    <div class="mt-5 rounded-xl border border-slate-800 bg-slate-950/40 p-4">
      <div class="flex items-center gap-3">
        <h2 class="text-sm font-bold text-slate-100">Edit metadata</h2>
        <span class="truncate font-mono text-xs text-slate-500">{editItem.relativePath}</span>
      </div>
      <div class="mt-4 grid gap-3 md:grid-cols-2">
        <Input bind:value={editTitle} placeholder="Title" class={inputClass} />
        <Input bind:value={editProvider} placeholder="Provider" class={inputClass} />
        <Input bind:value={editSourceMediaId} placeholder="Source media id" class={inputClass} />
        <Input bind:value={editSourceUrl} placeholder="Source URL" class={inputClass} />
      </div>
      <div class="mt-4 flex justify-end gap-2">
        <Button color="dark" class="border-slate-700! bg-slate-900! px-3! py-2! text-xs!" onclick={() => (editItem = null)} disabled={actionBusy}>
          Cancel
        </Button>
        <Button color="blue" class="border-0! px-3! py-2! text-xs!" onclick={saveEdit} disabled={actionBusy}>
          Save
        </Button>
      </div>
    </div>
  {/if}

  <div class="mt-5 overflow-hidden rounded-xl border border-slate-800">
    <table class="min-w-full divide-y divide-slate-800 text-left text-sm">
      <thead class="bg-slate-950/60 text-xs uppercase tracking-wide text-slate-500">
        <tr>
          <th class="w-10 px-4 py-3"></th>
          <th class="px-4 py-3">File</th>
          <th class="px-4 py-3">Title</th>
          <th class="px-4 py-3">Metadata</th>
          <th class="px-4 py-3">Size</th>
          <th class="px-4 py-3">Provider</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-800 bg-[#151a26]">
        {#if loading && items.length === 0}
          <tr><td colspan="7" class="px-4 py-8 text-center text-sm text-slate-500">Loading items</td></tr>
        {:else if items.length === 0}
          <tr><td colspan="7" class="px-4 py-8 text-center text-sm text-slate-500">No items found</td></tr>
        {:else}
          {#each items as item (item.itemId)}
            <tr class="hover:bg-slate-900/40" class:opacity-50={item.excluded}>
              <td class="px-4 py-3">
                <input
                  type="checkbox"
                  checked={selectedIds.includes(item.itemId)}
                  onchange={(event) => toggleSelected(item.itemId, (event.currentTarget as HTMLInputElement).checked)}
                  class="h-4 w-4 rounded border-slate-700 bg-slate-950 text-blue-500 focus:ring-blue-500"
                />
              </td>
              <td class="max-w-[24rem] px-4 py-3">
                <p class="truncate font-mono text-xs text-slate-300">{item.relativePath}</p>
              </td>
              <td class="max-w-[20rem] px-4 py-3">
                <p class="truncate text-slate-200">{item.title ?? item.fileName}</p>
              </td>
              <td class="px-4 py-3 text-slate-300">{item.metadataState}</td>
              <td class="px-4 py-3 text-xs text-slate-500">{formatBytes(item.fileSizeBytes)}</td>
              <td class="px-4 py-3 text-xs text-slate-500">{item.provider ?? 'local'}</td>
              <td class="px-4 py-3 text-right">
                <button type="button" class="text-xs font-semibold text-blue-300 hover:text-blue-200" onclick={() => startEdit(item)}>
                  Edit
                </button>
              </td>
            </tr>
          {/each}
        {/if}
      </tbody>
    </table>
  </div>

  {#if nextItemId}
    <div class="mt-4 flex justify-center">
      <Button color="dark" class="border-slate-700! bg-slate-900! px-4! py-2! text-xs!" onclick={() => load(nextItemId ?? undefined)} disabled={loading}>
        Load more ({items.length}/{totalCount})
      </Button>
    </div>
  {/if}
</section>
