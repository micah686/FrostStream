<script lang="ts">
  import { Badge } from 'flowbite-svelte';
  import { UsersGroupOutline } from 'flowbite-svelte-icons';

  let { data } = $props();

  const expiresLabel = $derived(data.expiresAt ? new Date(data.expiresAt).toLocaleString() : null);
</script>

<section class="grid gap-3 lg:grid-cols-3" aria-label="Account details">
  {#if data.user.email}
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Email</p>
      <p class="mt-1 truncate text-sm text-slate-300">{data.user.email}</p>
    </div>
  {/if}
  {#if expiresLabel}
    <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Session expires</p>
      <p class="mt-1 text-sm text-slate-300">{expiresLabel}</p>
    </div>
  {/if}
  <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
    <p class="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
      <UsersGroupOutline class="h-3.5 w-3.5" />
      Groups
    </p>
    <div class="mt-2 flex flex-wrap gap-1.5">
      {#each data.user.groups as group}
        <Badge rounded color="gray" class="bg-slate-800! px-2.5! py-0.5! text-xs! text-slate-300!">
          {group}
        </Badge>
      {:else}
        <span class="text-sm text-slate-500">No groups</span>
      {/each}
    </div>
  </div>
</section>
