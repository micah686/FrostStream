<script lang="ts">
  import {
    Check,
    ChevronDown,
    ChevronUp,
    CircleAlert,
    Pencil,
    Trash2
  } from '@lucide/svelte';
  import { deleteNote, getNote, saveNote, type NoteTargetType } from '$lib/api/notes';

  interface Props {
    targetType: NoteTargetType;
    targetId: string;
    targetLabel: string;
    initialNote?: string | null;
    onChange?: (note: string | null) => void;
    embedded?: boolean;
    initialOpen?: boolean;
  }

  let {
    targetType,
    targetId,
    targetLabel,
    initialNote = null,
    onChange,
    embedded = false,
    initialOpen = false
  }: Props = $props();


  let open = $state(false);
  let loadedTarget = $state('');
  let note = $state('');
  let draft = $state('');
  let loading = $state(false);
  let saving = $state(false);
  let deleting = $state(false);
  let error = $state<string | null>(null);
  let saved = $state(false);

  const hasNote = $derived(note.trim().length > 0);
  const dirty = $derived(draft.trim() !== note.trim());
  const currentKey = $derived(`${targetType}:${targetId}`);

  $effect(() => {
    const incoming = initialNote ?? '';
    // onChange feeds the loaded note back to the parent, which echoes it here via initialNote;
    // skip when nothing actually changed so that round-trip doesn't reset and reload the panel.
    if (incoming === note) {
      return;
    }
    note = incoming;
    draft = incoming;
    loadedTarget = '';
    error = null;
    saved = false;
  });

  $effect(() => {
    if (initialOpen) {
      open = true;
    }
  });

  $effect(() => {
    if (embedded && loadedTarget !== currentKey && !loading) {
      void load();
    }
  });

  async function toggleOpen() {
    open = !open;
    if (open && loadedTarget !== currentKey) {
      await load();
    }
  }

  async function load() {
    loading = true;
    error = null;
    try {
      const loaded = await getNote(targetType, targetId);
      note = loaded?.note ?? '';
      draft = note;
      loadedTarget = currentKey;
      onChange?.(note.trim() ? note : null);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load this note.';
      // Mark the target as attempted even on failure: the embedded $effect keys off loadedTarget,
      // and leaving it unset would re-trigger load() in a loop, hammering the API.
      loadedTarget = currentKey;
    } finally {
      loading = false;
    }
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (saving) {
      return;
    }
    const value = draft.trim();
    if (!value) {
      await remove();
      return;
    }

    saving = true;
    saved = false;
    error = null;
    try {
      const updated = await saveNote(targetType, targetId, value);
      note = updated.note;
      draft = updated.note;
      loadedTarget = currentKey;
      saved = true;
      onChange?.(updated.note);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not save this note.';
    } finally {
      saving = false;
    }
  }

  async function remove() {
    if (deleting || (!hasNote && !draft.trim())) {
      draft = '';
      return;
    }
    deleting = true;
    saved = false;
    error = null;
    try {
      await deleteNote(targetType, targetId);
      note = '';
      draft = '';
      loadedTarget = currentKey;
      onChange?.(null);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not delete this note.';
    } finally {
      deleting = false;
    }
  }
</script>

<section class={embedded ? '' : 'rounded-xl border border-base-content/30 bg-base-100'} aria-label={`${targetLabel} note`}>
  {#if !embedded}
    <button
      type="button"
      class="flex w-full items-center justify-between gap-3 px-4 py-3 text-left"
      aria-expanded={open}
      onclick={toggleOpen}
    >
      <span class="flex min-w-0 items-center gap-2">
        <Pencil class={['h-4 w-4 shrink-0', hasNote ? 'text-primary' : 'text-base-content/40']} />
        <span class="truncate text-sm font-semibold text-base-content/90">Note</span>
        {#if hasNote}
          <span class="badge badge-sm badge-primary font-semibold">Saved</span>
        {/if}
      </span>
      {#if open}
        <ChevronUp class="h-4 w-4 text-base-content/50" />
      {:else}
        <ChevronDown class="h-4 w-4 text-base-content/50" />
      {/if}
    </button>
  {/if}

  {#if open || embedded}
    <div class={embedded ? '' : 'border-t border-base-content/20 px-4 py-4'}>
      {#if loading}
        <div class="flex justify-center py-4">
          <span class="loading loading-spinner loading-sm"></span>
        </div>
      {:else}
        <form class="space-y-3" onsubmit={submit}>
          {#if error}
            <div
              class="flex items-start gap-2 rounded-lg border border-error/30 bg-error/10 p-3 text-sm text-error"
              role="alert"
            >
              <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
              <span>{error}</span>
            </div>
          {/if}

          <textarea class="textarea w-full" bind:value={draft} rows={4} maxlength={4096} placeholder={`Add a private note for this ${targetLabel.toLowerCase()}`}></textarea>

          <div class="flex flex-wrap items-center gap-2">
            <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={saving || deleting || (!dirty && hasNote)}>
              {#if saving}
                <span class="loading loading-spinner loading-xs mr-1.5"></span>
              {/if}
              Save note
            </button>
            <button class="btn btn-sm btn-ghost text-xs" type="button" disabled={saving || deleting || (!hasNote && !draft.trim())} onclick={remove}>
              {#if deleting}
                <span class="loading loading-spinner loading-xs mr-1.5"></span>
              {:else}
                <Trash2 class="mr-1.5 h-3.5 w-3.5" />
              {/if}
              Delete
            </button>
            {#if saved && !dirty}
              <span class="flex items-center gap-1.5 text-xs font-semibold text-success">
                <Check class="h-3.5 w-3.5" />
                Saved
              </span>
            {/if}
          </div>
        </form>
      {/if}
    </div>
  {/if}
</section>
