<script lang="ts">
  import { onMount } from 'svelte';

  let state = $state('ready');
  onMount(() => {
    const controller = new AbortController();
    let loading = false;
    async function refresh() {
      if (loading) return;
      loading = true;
      try {
        const response = await fetch('/api/search/status', { credentials: 'same-origin', signal: controller.signal });
        if (response.ok) {
          const status = await response.json() as { state: string | number };
          if (!controller.signal.aborted) {
            state = typeof status.state === 'number'
              ? ['building', 'ready', 'degraded', 'failed'][status.state] ?? 'failed'
              : status.state.toLowerCase();
          }
        }
      } catch { /* Requests are canceled when this view is left. */ }
      finally { loading = false; }
    }
    void refresh();
    const timer = setInterval(() => void refresh(), 10_000);
    return () => { clearInterval(timer); controller.abort(); };
  });
</script>

{#if state !== 'ready'}
  <div role="status" class="mb-4 rounded-lg border border-warning/40 bg-warning/10 p-3 text-sm">
    {#if state === 'building'}
      Search is rebuilding. Results may be incomplete until it finishes.
    {:else if state === 'degraded'}
      Search needs attention. The last working index is available, but recent changes may be missing.
    {:else}
      Search is currently unavailable. Your library has not been deleted.
    {/if}
  </div>
{/if}
