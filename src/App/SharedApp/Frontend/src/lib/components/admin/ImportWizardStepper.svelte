<script lang="ts">
  import { goto } from '$app/navigation';
  interface Props { current: number; sessionId?: string; }
  let { current, sessionId }: Props = $props();
  const steps = [
    { id: 1, label: 'Source', description: 'Choose a folder' },
    { id: 2, label: 'Files', description: 'Select media' },
    { id: 3, label: 'Metadata', description: 'Fetch info.json' },
    { id: 4, label: 'Mapping', description: 'Add manual data' },
    { id: 5, label: 'Review', description: 'Verify sources' },
    { id: 6, label: 'Import', description: 'Run and monitor' }
  ];
  const paths = ['source', 'files', 'metadata', 'mapping', 'review', 'run'];

  function navigate(event: { current: number }) {
    if (event.current === 1) void goto('/admin/import/new/source');
    else if (sessionId) void goto(`/admin/import/${sessionId}/${paths[event.current - 1]}`);
  }
</script>

<div class="mb-6 overflow-x-auto">
  <ul class="steps steps-horizontal min-w-[760px] w-full">
    {#each steps as step}
      <li class:step-primary={step.id <= current} class="step">
        {#if sessionId || step.id === 1}
          <button type="button" class="text-xs font-semibold" onclick={() => navigate({ current: step.id })}>{step.label}</button>
        {:else}
          <span class="text-xs font-semibold">{step.label}</span>
        {/if}
      </li>
    {/each}
  </ul>
</div>
