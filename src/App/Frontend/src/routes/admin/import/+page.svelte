<script lang="ts">
  import { Button } from 'flowbite-svelte';
  import { FileImportOutline } from 'flowbite-svelte-icons';

  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
</script>

<section class={card}>
  <div class="flex flex-col items-start gap-5 sm:flex-row sm:items-center">
    <div>
      <h1 class="text-xl font-bold text-white">Import local media</h1>
      <p class="mt-2 max-w-2xl text-sm leading-6 text-slate-400">Choose files from a worker’s incoming folder, attach the best available metadata, review everything, and monitor the import.</p>
    </div>
    <a href="/admin/import/new/source" class="sm:ml-auto">
      <Button color="blue" size="lg" class="border-0! px-6! font-semibold!"><FileImportOutline class="mr-2 h-5 w-5" />Start import</Button>
    </a>
  </div>
</section>
