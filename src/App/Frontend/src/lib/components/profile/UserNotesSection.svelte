<script lang="ts">
  import { onMount } from 'svelte';
  import { Select } from '$lib/components/ui';
  import {
    Check,
    CircleAlert,
    Eye,
    FileSearch,
    Pencil,
    Search,
    Trash2
  } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    deleteNote,
    saveNote,
    searchNotes,
    type NoteTargetType,
    type UserNote
  } from '$lib/api/notes';


  const targetOptions = [
    { value: 'all', name: 'All targets' },
    { value: 'video', name: 'Videos' },
    { value: 'playlist', name: 'Playlists' },
    { value: 'channel', name: 'Channels' }
  ];

  let notes = $state<UserNote[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let query = $state('');
  let targetType = $state<'all' | NoteTargetType>('all');
  let page = $state(1);
  let totalCount = $state(0);
  let hasMore = $state(false);
  let editingKey = $state<string | null>(null);
  let draft = $state('');
  let savingKey = $state<string | null>(null);
  let savedKey = $state<string | null>(null);
  let deleteModalOpen = $state(false);
  let pendingDelete = $state<UserNote | null>(null);
  let deletingKey = $state<string | null>(null);

  const pageSize = 25;
  const totalPages = $derived(Math.max(1, Math.ceil(totalCount / pageSize)));

  onMount(() => {
    void loadNotes(1);
  });

  async function loadNotes(targetPage = 1) {
    loading = true;
    error = null;
    try {
      const result = await searchNotes({
        query,
        targetType,
        page: targetPage,
        pageSize
      });
      notes = result.items;
      page = result.page;
      totalCount = result.totalCount;
      hasMore = result.hasMore;
      editingKey = null;
      savedKey = null;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load notes.';
    } finally {
      loading = false;
    }
  }

  function submitSearch(event: SubmitEvent) {
    event.preventDefault();
    void loadNotes(1);
  }

  function noteKey(note: UserNote): string {
    return `${note.targetType}:${note.targetId}`;
  }

  function startEdit(note: UserNote) {
    editingKey = noteKey(note);
    draft = note.note;
    savedKey = null;
  }

  async function saveCurrent(note: UserNote) {
    const value = draft.trim();
    if (!value) {
      pendingDelete = note;
      deleteModalOpen = true;
      return;
    }
    const key = noteKey(note);
    savingKey = key;
    error = null;
    try {
      const updated = await saveNote(note.targetType, note.targetId, value);
      notes = notes.map((item) => (noteKey(item) === key ? { ...item, ...updated } : item));
      editingKey = null;
      savedKey = key;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not save the note.';
    } finally {
      savingKey = null;
    }
  }

  async function confirmDelete() {
    const note = pendingDelete;
    if (!note) {
      return;
    }
    const key = noteKey(note);
    deletingKey = key;
    await deleteNote(note.targetType, note.targetId);
    notes = notes.filter((item) => noteKey(item) !== key);
    totalCount = Math.max(0, totalCount - 1);
    if (editingKey === key) {
      editingKey = null;
    }
    pendingDelete = null;
    deletingKey = null;
  }

  function targetHref(note: UserNote): string {
    switch (note.targetType) {
      case 'channel':
        return `/channel/${encodeURIComponent(note.targetId)}`;
      case 'playlist':
        return `/playlists?playlist=${encodeURIComponent(note.targetId)}`;
      default:
        return `/watch/${encodeURIComponent(note.targetId)}`;
    }
  }

  function targetLabel(note: UserNote): string {
    switch (note.targetType) {
      case 'channel':
        return 'Channel';
      case 'playlist':
        return 'Playlist';
      default:
        return 'Video';
    }
  }

  function displayDate(value: string | null): string | null {
    if (!value) {
      return null;
    }
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    return date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  }
</script>

<section class="card border border-base-300 bg-base-100 p-5 sm:p-6">
  <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 class="text-base font-bold text-base-content">Notes</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Private notes saved against videos, playlists, and channels.
      </p>
    </div>
    <span class="rounded-full bg-base-300 px-2.5 py-1 text-[10px] font-semibold text-base-content/60">
      {totalCount} {totalCount === 1 ? 'note' : 'notes'}
    </span>
  </div>

  <form class="mt-5 grid gap-3 lg:grid-cols-[minmax(0,1fr)_12rem_auto]" onsubmit={submitSearch}>
    <div class="relative">
      <Search class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-base-content/40" />
      <input class="input w-full pl-9" bind:value={query} placeholder="Search notes" />
    </div>
    <Select items={targetOptions} bind:value={targetType} />
    <button class="btn btn-sm btn-neutral text-xs" type="submit" disabled={loading}>
      {#if loading}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {/if}
      Search
    </button>
  </form>

  {#if error}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error}</span>
    </div>
  {/if}

  {#if loading && notes.length === 0}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if notes.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <FileSearch class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No notes found</p>
      <p class="mt-1 text-sm text-base-content/50">Notes you add from videos, playlists, or channels appear here.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each notes as note (noteKey(note))}
        {@const key = noteKey(note)}
        {@const editing = editingKey === key}
        <article class="rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:px-4">
          <div class="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <span class="rounded-full bg-primary/10 px-2 py-0.5 text-[10px] font-bold uppercase text-primary">
                  {targetLabel(note)}
                </span>
                <h3 class="truncate text-sm font-semibold text-base-content">
                  {note.targetTitle ?? note.targetId}
                </h3>
                {#if displayDate(note.updatedAt ?? note.createdAt)}
                  <span class="text-xs text-base-content/40">updated {displayDate(note.updatedAt ?? note.createdAt)}</span>
                {/if}
              </div>
              {#if note.targetSubtitle}
                <p class="mt-1 truncate text-xs text-base-content/50">{note.targetSubtitle}</p>
              {/if}
            </div>

            <div class="flex shrink-0 flex-wrap gap-2">
              <a
                href={targetHref(note)}
                class="inline-flex h-9 min-w-20 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary"
              >
                <Eye class="h-4 w-4" />
                View
              </a>
              <button
                type="button"
                class="inline-flex h-9 min-w-20 items-center justify-center gap-1.5 rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-xs font-semibold text-base-content/90 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary"
                onclick={() => (editing ? (editingKey = null) : startEdit(note))}
              >
                <Pencil class="h-4 w-4" />
                {editing ? 'Close' : 'Edit'}
              </button>
              <button
                type="button"
                class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-base-content/80 transition hover:border-error/60 hover:bg-error/10 hover:text-error disabled:opacity-50"
                title="Delete note"
                aria-label={`Delete note for ${note.targetTitle ?? note.targetId}`}
                disabled={deletingKey === key}
                onclick={() => {
                  pendingDelete = note;
                  deleteModalOpen = true;
                }}
              >
                {#if deletingKey === key}
                  <span class="loading loading-spinner loading-xs"></span>
                {:else}
                  <Trash2 class="h-4 w-4" />
                {/if}
              </button>
            </div>
          </div>

          {#if editing}
            <div class="mt-3 space-y-3">
              <textarea class="textarea w-full" bind:value={draft} rows={4} maxlength={4096}></textarea>
              <div class="flex flex-wrap items-center gap-2">
                <button class="btn btn-sm btn-primary text-xs" disabled={savingKey === key || draft.trim() === note.note.trim()} onclick={() => saveCurrent(note)}>
                  {#if savingKey === key}
                    <span class="loading loading-spinner loading-xs mr-1.5"></span>
                  {/if}
                  Save changes
                </button>
                <button class="btn btn-sm btn-ghost text-xs" onclick={() => (editingKey = null)}>
                  Cancel
                </button>
              </div>
            </div>
          {:else}
            <p class="mt-3 line-clamp-3 whitespace-pre-line text-sm leading-6 text-base-content/80">{note.note}</p>
          {/if}

          {#if savedKey === key}
            <p class="mt-2 flex items-center gap-1.5 text-xs font-semibold text-success">
              <Check class="h-3.5 w-3.5" />
              Saved
            </p>
          {/if}
        </article>
      {/each}
    </div>

    <div class="mt-5 flex items-center justify-between border-t border-base-300/70 pt-4">
      <p class="text-xs text-base-content/40">Page {page} of {totalPages}</p>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-ghost text-xs" disabled={page <= 1 || loading} onclick={() => loadNotes(page - 1)}>
          Previous
        </button>
        <button class="btn btn-sm btn-ghost text-xs" disabled={!hasMore || loading} onclick={() => loadNotes(page + 1)}>
          Next
        </button>
      </div>
    </div>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete note"
  message={pendingDelete ? `Delete the note for "${pendingDelete.targetTitle ?? pendingDelete.targetId}"?` : ''}
  confirmLabel="Delete note"
  onConfirm={confirmDelete}
/>
