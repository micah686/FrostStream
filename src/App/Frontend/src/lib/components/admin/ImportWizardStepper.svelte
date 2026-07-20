<script lang="ts">
  import { goto } from '$app/navigation';
  import { DetailedStepper } from 'flowbite-svelte';

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

<div class="mb-6 overflow-x-auto rounded-2xl border border-slate-800 bg-[#151a26] p-4">
  <DetailedStepper {steps} {current} clickable={!!sessionId || current === 1} onStepClick={navigate} class="min-w-[760px]" />
</div>
