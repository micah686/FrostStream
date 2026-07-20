<script lang="ts">
  import { ExclamationCircleOutline } from 'flowbite-svelte-icons';
  interface Props { error?: string | null; notice?: string | null; }
  let { error = null, notice = null }: Props = $props();
</script>
{#if error}
  <div class="mb-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"><ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />{error}</div>
{/if}
{#if notice}<div class="mb-5 rounded-xl border border-emerald-900/60 bg-emerald-950/25 p-3 text-sm text-emerald-300">{notice}</div>{/if}
