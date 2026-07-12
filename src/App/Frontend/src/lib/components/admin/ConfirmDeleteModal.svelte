<script lang="ts">
  import { Button, Modal, Spinner } from 'flowbite-svelte';
  import { ExclamationCircleOutline } from 'flowbite-svelte-icons';

  interface Props {
    open: boolean;
    title: string;
    message: string;
    confirmLabel: string;
    onConfirm: () => Promise<void>;
    destructive?: boolean;
  }

  let {
    open = $bindable(false),
    title,
    message,
    confirmLabel,
    onConfirm,
    destructive = true
  }: Props = $props();

  let busy = $state(false);
  let error = $state<string | null>(null);

  async function confirm() {
    busy = true;
    error = null;
    try {
      await onConfirm();
      open = false;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Action failed.';
    } finally {
      busy = false;
    }
  }
</script>

<Modal bind:open title={title} size="md" class="z-50">
  <div class="space-y-4">
    <div class="flex items-start gap-3">
      <span class="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-red-500/10 text-red-300 ring-1 ring-red-500/20">
        <ExclamationCircleOutline class="h-5 w-5" />
      </span>
      <div class="min-w-0">
        <p class="text-sm text-slate-300">{message}</p>
        {#if error}
          <p class="mt-2 text-sm text-red-300">{error}</p>
        {/if}
      </div>
    </div>
  </div>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <Button
        color="dark"
        class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
        disabled={busy}
        onclick={() => (open = false)}
      >
        Cancel
      </Button>
      <Button
        color={destructive ? 'red' : 'blue'}
        class="border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
        disabled={busy}
        onclick={confirm}
      >
        {#if busy}
          <Spinner size="4" class="mr-1.5" />
        {/if}
        {confirmLabel}
      </Button>
    </div>
  {/snippet}
</Modal>
