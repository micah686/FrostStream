<script lang="ts">
  import { Modal } from '$lib/components/ui';
  import { ArrowLeft, ChevronRight, CircleAlert, Folder } from '@lucide/svelte';
  import { browseImportIncoming } from '$lib/api/imports';

  interface Props { open: boolean; workerTag?: string; initialPath?: string; onselect: (path: string) => void; }
  let { open = $bindable(false), workerTag = '', initialPath = '', onselect }: Props = $props();
  let currentPath = $state('');
  let directories = $state<string[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let wasOpen = $state(false);

  $effect(() => {
    const opened = open && !wasOpen;
    wasOpen = open;
    if (opened) void navigate(initialPath, true);
  });

  async function navigate(path: string, fallback = false) {
    loading = true; error = null;
    try {
      const listing = await browseImportIncoming(path || undefined, workerTag || undefined);
      currentPath = listing.subPath;
      directories = listing.directories;
    } catch (err) {
      if (fallback && path) { await navigate(''); return; }
      error = err instanceof Error ? err.message : 'Could not list this folder.';
    } finally { loading = false; }
  }
</script>

<Modal bind:open title="Choose incoming folder" size="md">
  <div class="space-y-3">
    <div class="flex items-center gap-2 rounded-lg border border-base-300 bg-base-200/60 px-3 py-2.5">
      <Folder class="h-4 w-4 text-base-content/50" />
      <span class="truncate font-mono text-sm text-base-content/90">incoming/{currentPath}</span>
      {#if loading}<span class="loading loading-spinner loading-xs ml-auto"></span>{/if}
    </div>
    {#if error}
      <div class="alert alert-error text-sm" role="alert">
        <CircleAlert class="h-4 w-4 shrink-0" />{error}
      </div>
    {/if}
    <div class="max-h-72 overflow-y-auto rounded-xl border border-base-300 bg-base-200/40">
      {#if currentPath}
        <button type="button" class="flex w-full items-center gap-2 border-b border-base-300 px-4 py-3 text-left text-sm text-base-content/60 hover:bg-base-200" onclick={() => navigate(currentPath.split('/').slice(0, -1).join('/'))}>
          <ArrowLeft class="h-4 w-4" /> Up one level
        </button>
      {/if}
      {#each directories as name (name)}
        <button type="button" class="flex w-full items-center justify-between border-b border-base-300/60 px-4 py-3 text-left text-sm text-base-content/90 last:border-0 hover:bg-base-200" onclick={() => navigate(currentPath ? `${currentPath}/${name}` : name)}>
          <span class="truncate">{name}</span><ChevronRight class="h-4 w-4 text-base-content/50" />
        </button>
      {:else}
        {#if !loading}<p class="px-4 py-6 text-center text-sm text-base-content/50">No sub-folders here.</p>{/if}
      {/each}
    </div>
  </div>
  {#snippet footer()}
    <div class="flex w-full justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" onclick={() => (open = false)}>Cancel</button>
      <button class="btn btn-sm btn-primary text-xs" disabled={loading || !!error} onclick={() => { onselect(currentPath); open = false; }}>Select folder</button>
    </div>
  {/snippet}
</Modal>
