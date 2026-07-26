<script lang="ts">
  import type { Snippet } from 'svelte';

  interface Props {
    open: boolean;
    placement?: 'left' | 'right';
    class?: string;
    label?: string;
    children?: Snippet;
  }

  let {
    open = $bindable(false),
    placement = 'left',
    class: klass = '',
    label = 'Navigation',
    children
  }: Props = $props();

  // daisyUI's `drawer` is a layout primitive that reserves page width for the
  // sidebar. This is an overlay instead, so it is hand-rolled off `open`.
  const edge = $derived(placement === 'left' ? 'left-0' : 'right-0');
  const hidden = $derived(placement === 'left' ? '-translate-x-full' : 'translate-x-full');
</script>

<svelte:window
  onkeydown={(event) => {
    if (event.key === 'Escape' && open) open = false;
  }}
/>

<div class={['fixed inset-0 z-50', open ? '' : 'pointer-events-none']} aria-hidden={!open}>
  <button
    type="button"
    tabindex={open ? 0 : -1}
    aria-label="Close {label.toLowerCase()}"
    class={[
      'absolute inset-0 bg-black/60 transition-opacity duration-200',
      open ? 'opacity-100' : 'opacity-0'
    ]}
    onclick={() => (open = false)}
  ></button>

  <aside
    aria-label={label}
    class={[
      'absolute inset-y-0 flex flex-col bg-base-200 shadow-2xl transition-transform duration-200',
      edge,
      placement === 'left' ? 'border-r' : 'border-l',
      'border-base-300',
      open ? 'translate-x-0' : hidden,
      klass
    ]}
  >
    {@render children?.()}
  </aside>
</div>
