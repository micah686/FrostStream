<script lang="ts">
  import { Button, Modal, Spinner } from 'flowbite-svelte';
  import { ArrowLeftOutline, ChevronRightOutline, ExclamationCircleOutline, FolderOutline } from 'flowbite-svelte-icons';
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

<Modal bind:open title="Choose incoming folder" size="md" class="z-50">
  <div class="space-y-3">
    <div class="flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-950/60 px-3 py-2.5">
      <FolderOutline class="h-4 w-4 text-slate-500" />
      <span class="truncate font-mono text-sm text-slate-200">incoming/{currentPath}</span>
      {#if loading}<Spinner size="4" class="ml-auto" />{/if}
    </div>
    {#if error}
      <div class="flex gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300">
        <ExclamationCircleOutline class="h-4 w-4 shrink-0" />{error}
      </div>
    {/if}
    <div class="max-h-72 overflow-y-auto rounded-xl border border-slate-800 bg-slate-950/40">
      {#if currentPath}
        <button type="button" class="flex w-full items-center gap-2 border-b border-slate-800 px-4 py-3 text-left text-sm text-slate-400 hover:bg-slate-900" onclick={() => navigate(currentPath.split('/').slice(0, -1).join('/'))}>
          <ArrowLeftOutline class="h-4 w-4" /> Up one level
        </button>
      {/if}
      {#each directories as name (name)}
        <button type="button" class="flex w-full items-center justify-between border-b border-slate-800/60 px-4 py-3 text-left text-sm text-slate-200 last:border-0 hover:bg-slate-900" onclick={() => navigate(currentPath ? `${currentPath}/${name}` : name)}>
          <span class="truncate">{name}</span><ChevronRightOutline class="h-4 w-4 text-slate-500" />
        </button>
      {:else}
        {#if !loading}<p class="px-4 py-6 text-center text-sm text-slate-500">No sub-folders here.</p>{/if}
      {/each}
    </div>
  </div>
  {#snippet footer()}
    <div class="flex w-full justify-end gap-2">
      <Button color="dark" class="border-slate-700! bg-transparent! text-xs!" onclick={() => (open = false)}>Cancel</Button>
      <Button color="blue" class="border-0! text-xs!" disabled={loading || !!error} onclick={() => { onselect(currentPath); open = false; }}>Select folder</Button>
    </div>
  {/snippet}
</Modal>
