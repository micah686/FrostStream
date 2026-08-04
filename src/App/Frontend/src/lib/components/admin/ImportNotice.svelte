<script lang="ts">
import { CircleAlert } from '@lucide/svelte';
  interface Props { error?: string | null; notice?: string | null; }
  let { error = null, notice = null }: Props = $props();
</script>
{#if error}
  <div class="alert alert-error mb-5 text-sm" role="alert"><CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />{error}</div>
{/if}
{#if notice}<div class="alert alert-info mb-5 text-sm" role="status">{notice}</div>{/if}
