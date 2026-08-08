<script lang="ts">
  import { FileInput } from '@lucide/svelte';

  const card = 'card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6';
</script>

<section class={card}>
  <div class="flex flex-col items-start gap-5 sm:flex-row sm:items-center">
    <div>
      <h1 class="text-xl font-bold text-base-content">Import local media</h1>
      <p class="mt-2 max-w-2xl text-sm leading-6 text-base-content/60">Choose files from a worker’s incoming folder, attach the best available metadata, review everything, and monitor the import.</p>
    </div>
    <a href="/admin/import/new/source" class="sm:ml-auto">
      <button class="btn btn-sm btn-primary"><FileInput class="mr-2 h-5 w-5" />Start import</button>
    </a>
  </div>
</section>
