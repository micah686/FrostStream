<script lang="ts">
  import { ExclamationCircleOutline } from 'flowbite-svelte-icons';

  interface Props {
    title?: string;
    message?: string;
  }

  let {
    title = 'Under development',
    message = 'This area is still being built. Expect changes in layout and behavior.'
  }: Props = $props();
</script>

<div
  class="flex items-start gap-3 rounded-xl border border-amber-700/60 bg-amber-950/30 px-4 py-3 text-sm text-amber-100"
  role="note"
>
  <span class="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-amber-500/15 text-amber-300">
    <ExclamationCircleOutline class="h-4 w-4" />
  </span>
  <div class="min-w-0">
    <p class="text-xs font-semibold uppercase tracking-wide text-amber-300">{title}</p>
    <p class="mt-1 text-sm leading-6 text-amber-100/90">{message}</p>
  </div>
</div>
