<script lang="ts">
  import type { Snippet } from 'svelte';
  import { CloseOutline } from 'flowbite-svelte-icons';

  type Size = 'sm' | 'md' | 'lg' | 'xl';

  interface Props {
    open: boolean;
    title?: string;
    size?: Size;
    class?: string;
    children?: Snippet;
    footer?: Snippet;
  }

  let {
    open = $bindable(false),
    title,
    size = 'md',
    class: klass = '',
    children,
    footer
  }: Props = $props();

  const sizeClass: Record<Size, string> = {
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl'
  };

  let dialog = $state<HTMLDialogElement | null>(null);

  // showModal()/close() are imperative, so mirror `open` onto the element. The
  // guards matter: calling showModal() on an already-open dialog throws.
  $effect(() => {
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    else if (!open && dialog.open) dialog.close();
  });
</script>

<dialog bind:this={dialog} class="modal" onclose={() => (open = false)}>
  <div class={['modal-box w-full p-0', sizeClass[size], klass]}>
    {#if title}
      <div class="flex items-start justify-between gap-4 border-b border-base-300 px-5 py-4">
        <h3 class="text-base font-semibold text-base-content">{title}</h3>
        <button
          type="button"
          class="btn btn-ghost btn-sm btn-circle -mr-1 -mt-1"
          aria-label="Close"
          onclick={() => (open = false)}
        >
          <CloseOutline class="h-4 w-4" />
        </button>
      </div>
    {/if}

    <div class="px-5 py-5">
      {@render children?.()}
    </div>

    {#if footer}
      <div class="border-t border-base-300 px-5 py-4">
        {@render footer()}
      </div>
    {/if}
  </div>

  <form method="dialog" class="modal-backdrop">
    <button aria-label="Close">close</button>
  </form>
</dialog>
