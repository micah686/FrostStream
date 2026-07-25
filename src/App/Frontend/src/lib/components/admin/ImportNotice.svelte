<script lang="ts">
  import { CircleAlert } from '@lucide/svelte';
  interface Props { error?: string | null; notice?: string | null; }
  let { error = null, notice = null }: Props = $props();
</script>
{#if error}
  <div class="mb-5 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"><CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />{error}</div>
{/if}
{#if notice}<div class="mb-5 rounded-xl border border-success/30 bg-success/10 p-3 text-sm text-success">{notice}</div>{/if}
