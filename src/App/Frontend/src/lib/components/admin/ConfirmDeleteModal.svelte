<script lang="ts">
  import { Modal } from '$lib/components/ui';
  import { CircleAlert } from '@lucide/svelte';

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

<Modal bind:open {title} size="md">
  <div class="space-y-4">
    <div class="flex items-start gap-3">
      <span class="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-error/10 text-error ring-1 ring-error/20">
        <CircleAlert class="h-5 w-5" />
      </span>
      <div class="min-w-0">
        <p class="text-sm text-base-content/80">{message}</p>
        {#if error}
          <p class="mt-2 text-sm text-error">{error}</p>
        {/if}
      </div>
    </div>
  </div>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" disabled={busy} onclick={() => (open = false)}>
        Cancel
      </button>
      <button
        class={['btn btn-sm text-xs', destructive ? 'btn-error' : 'btn-primary']}
        disabled={busy}
        onclick={confirm}
      >
        {#if busy}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {/if}
        {confirmLabel}
      </button>
    </div>
  {/snippet}
</Modal>
