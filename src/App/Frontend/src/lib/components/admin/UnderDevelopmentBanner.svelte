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
  class="flex items-start gap-3 rounded-xl border border-warning/60 bg-warning/10 px-4 py-3 text-sm text-warning"
  role="note"
>
  <span class="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-warning/15 text-warning">
    <ExclamationCircleOutline class="h-4 w-4" />
  </span>
  <div class="min-w-0">
    <p class="text-xs font-semibold uppercase tracking-wide text-warning">{title}</p>
    <p class="mt-1 text-sm leading-6 text-warning">{message}</p>
  </div>
</div>
