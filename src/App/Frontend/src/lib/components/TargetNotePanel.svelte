<script lang="ts">
  import { Button, Spinner, Textarea } from 'flowbite-svelte';
  import {
    CheckOutline,
    ChevronDownOutline,
    ChevronUpOutline,
    EditOutline,
    ExclamationCircleOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
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

  const fieldClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';

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

<section class={embedded ? '' : 'rounded-xl border border-slate-800/80 bg-slate-900/35'} aria-label={`${targetLabel} note`}>
  {#if !embedded}
    <button
      type="button"
      class="flex w-full items-center justify-between gap-3 px-4 py-3 text-left"
      aria-expanded={open}
      onclick={toggleOpen}
    >
      <span class="flex min-w-0 items-center gap-2">
        <EditOutline class={['h-4 w-4 shrink-0', hasNote ? 'text-blue-400' : 'text-slate-600']} />
        <span class="truncate text-sm font-semibold text-slate-200">Note</span>
        {#if hasNote}
          <span class="rounded-full bg-blue-500/10 px-2 py-0.5 text-[10px] font-semibold text-blue-300">Saved</span>
        {/if}
      </span>
      {#if open}
        <ChevronUpOutline class="h-4 w-4 text-slate-500" />
      {:else}
        <ChevronDownOutline class="h-4 w-4 text-slate-500" />
      {/if}
    </button>
  {/if}

  {#if open || embedded}
    <div class={embedded ? '' : 'border-t border-slate-800/70 px-4 py-4'}>
      {#if loading}
        <div class="flex justify-center py-4">
          <Spinner size="5" />
        </div>
      {:else}
        <form class="space-y-3" onsubmit={submit}>
          {#if error}
            <div
              class="flex items-start gap-2 rounded-lg border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
              role="alert"
            >
              <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
              <span>{error}</span>
            </div>
          {/if}

          <Textarea
            bind:value={draft}
            rows={4}
            maxlength={4096}
            placeholder={`Add a private note for this ${targetLabel.toLowerCase()}`}
            class={fieldClass}
          />

          <div class="flex flex-wrap items-center gap-2">
            <Button
              type="submit"
              color="blue"
              disabled={saving || deleting || (!dirty && hasNote)}
              class="border-0! bg-blue-500! px-3! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
            >
              {#if saving}
                <Spinner size="4" class="mr-1.5" />
              {/if}
              Save note
            </Button>
            <Button
              type="button"
              color="dark"
              disabled={saving || deleting || (!hasNote && !draft.trim())}
              onclick={remove}
              class="border-slate-700! bg-transparent! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:border-red-500/60! hover:bg-red-500/10! hover:text-red-200! disabled:opacity-60"
            >
              {#if deleting}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <TrashBinOutline class="mr-1.5 h-3.5 w-3.5" />
              {/if}
              Delete
            </Button>
            {#if saved && !dirty}
              <span class="flex items-center gap-1.5 text-xs font-semibold text-emerald-400">
                <CheckOutline class="h-3.5 w-3.5" />
                Saved
              </span>
            {/if}
          </div>
        </form>
      {/if}
    </div>
  {/if}
</section>
