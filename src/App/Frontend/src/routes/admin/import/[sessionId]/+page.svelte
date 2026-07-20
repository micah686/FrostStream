<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  onMount(() => { void goto(`/admin/import/${page.params.sessionId}/files`, { replaceState: true }); });
</script>
<p class="text-sm text-slate-500">Opening import session…</p>
