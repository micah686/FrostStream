<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Select, Spinner, Textarea } from 'flowbite-svelte';
  import {
    CheckOutline,
    EditOutline,
    ExclamationCircleOutline,
    EyeOutline,
    FileSearchOutline,
    SearchOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    deleteNote,
    saveNote,
    searchNotes,
    type NoteTargetType,
    type UserNote
  } from '$lib/api/notes';

  const fieldClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';

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

<section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
  <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 class="text-base font-bold text-slate-100">Notes</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Private notes saved against videos, playlists, and channels.
      </p>
    </div>
    <span class="rounded-full bg-slate-800 px-2.5 py-1 text-[10px] font-semibold text-slate-400">
      {totalCount} {totalCount === 1 ? 'note' : 'notes'}
    </span>
  </div>

  <form class="mt-5 grid gap-3 lg:grid-cols-[minmax(0,1fr)_12rem_auto]" onsubmit={submitSearch}>
    <div class="relative">
      <SearchOutline class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-600" />
      <Input bind:value={query} placeholder="Search notes" class={`${fieldClass} pl-9!`} />
    </div>
    <Select items={targetOptions} bind:value={targetType} class={fieldClass} />
    <Button
      type="submit"
      color="dark"
      disabled={loading}
      class="border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
    >
      {#if loading}
        <Spinner size="4" class="mr-1.5" />
      {/if}
      Search
    </Button>
  </form>

  {#if error}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error}</span>
    </div>
  {/if}

  {#if loading && notes.length === 0}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if notes.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <FileSearchOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No notes found</p>
      <p class="mt-1 text-sm text-slate-500">Notes you add from videos, playlists, or channels appear here.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each notes as note (noteKey(note))}
        {@const key = noteKey(note)}
        {@const editing = editingKey === key}
        <article class="rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:px-4">
          <div class="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <span class="rounded-full bg-blue-500/10 px-2 py-0.5 text-[10px] font-bold uppercase text-blue-300">
                  {targetLabel(note)}
                </span>
                <h3 class="truncate text-sm font-semibold text-slate-100">
                  {note.targetTitle ?? note.targetId}
                </h3>
                {#if displayDate(note.updatedAt ?? note.createdAt)}
                  <span class="text-xs text-slate-600">updated {displayDate(note.updatedAt ?? note.createdAt)}</span>
                {/if}
              </div>
              {#if note.targetSubtitle}
                <p class="mt-1 truncate text-xs text-slate-500">{note.targetSubtitle}</p>
              {/if}
            </div>

            <div class="flex shrink-0 flex-wrap gap-2">
              <a
                href={targetHref(note)}
                class="inline-flex h-9 min-w-20 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
              >
                <EyeOutline class="h-4 w-4" />
                View
              </a>
              <button
                type="button"
                class="inline-flex h-9 min-w-20 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
                onclick={() => (editing ? (editingKey = null) : startEdit(note))}
              >
                <EditOutline class="h-4 w-4" />
                {editing ? 'Close' : 'Edit'}
              </button>
              <button
                type="button"
                class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                title="Delete note"
                aria-label={`Delete note for ${note.targetTitle ?? note.targetId}`}
                disabled={deletingKey === key}
                onclick={() => {
                  pendingDelete = note;
                  deleteModalOpen = true;
                }}
              >
                {#if deletingKey === key}
                  <Spinner size="4" />
                {:else}
                  <TrashBinOutline class="h-4 w-4" />
                {/if}
              </button>
            </div>
          </div>

          {#if editing}
            <div class="mt-3 space-y-3">
              <Textarea bind:value={draft} rows={4} maxlength={4096} class={fieldClass} />
              <div class="flex flex-wrap items-center gap-2">
                <Button
                  color="blue"
                  disabled={savingKey === key || draft.trim() === note.note.trim()}
                  onclick={() => saveCurrent(note)}
                  class="border-0! bg-blue-500! px-3! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
                >
                  {#if savingKey === key}
                    <Spinner size="4" class="mr-1.5" />
                  {/if}
                  Save changes
                </Button>
                <Button
                  color="dark"
                  onclick={() => (editingKey = null)}
                  class="border-slate-700! bg-transparent! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
                >
                  Cancel
                </Button>
              </div>
            </div>
          {:else}
            <p class="mt-3 line-clamp-3 whitespace-pre-line text-sm leading-6 text-slate-300">{note.note}</p>
          {/if}

          {#if savedKey === key}
            <p class="mt-2 flex items-center gap-1.5 text-xs font-semibold text-emerald-400">
              <CheckOutline class="h-3.5 w-3.5" />
              Saved
            </p>
          {/if}
        </article>
      {/each}
    </div>

    <div class="mt-5 flex items-center justify-between border-t border-slate-800/70 pt-4">
      <p class="text-xs text-slate-600">Page {page} of {totalPages}</p>
      <div class="flex gap-2">
        <Button
          color="dark"
          disabled={page <= 1 || loading}
          onclick={() => loadNotes(page - 1)}
          class="border-slate-700! bg-transparent! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
        >
          Previous
        </Button>
        <Button
          color="dark"
          disabled={!hasMore || loading}
          onclick={() => loadNotes(page + 1)}
          class="border-slate-700! bg-transparent! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
        >
          Next
        </Button>
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
