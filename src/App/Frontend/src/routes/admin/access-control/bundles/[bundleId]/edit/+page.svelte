<script lang="ts">
  import { page } from '$app/state';
  import BundleManagementSection from '$lib/components/admin/BundleManagementSection.svelte';
</script>

<svelte:head>
  <title>Edit endpoint membership · FrostStream</title>
</svelte:head>

<BundleManagementSection mode="edit" bundleId={page.params.bundleId} />
