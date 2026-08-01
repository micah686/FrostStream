<script lang="ts">
  import SchedulesSection from '$lib/components/admin/SchedulesSection.svelte';

  let { params } = $props();
</script>

<svelte:head><title>Edit schedule · FrostStream</title></svelte:head>

<SchedulesSection editKey={params.key} />
