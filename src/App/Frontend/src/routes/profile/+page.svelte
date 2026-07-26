<script lang="ts">
  import { Users } from '@lucide/svelte';

  let { data } = $props();

  const expiresLabel = $derived(data.expiresAt ? new Date(data.expiresAt).toLocaleString() : null);
</script>

<section class="grid gap-3 lg:grid-cols-3" aria-label="Account details">
  {#if data.user.email}
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Email</p>
      <p class="mt-1 truncate text-sm text-base-content/80">{data.user.email}</p>
    </div>
  {/if}
  {#if expiresLabel}
    <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Session expires</p>
      <p class="mt-1 text-sm text-base-content/80">{expiresLabel}</p>
    </div>
  {/if}
  <div class="rounded-xl border border-base-300/80 bg-base-200/40 p-4">
    <p class="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">
      <Users class="h-3.5 w-3.5" />
      Groups
    </p>
    <div class="mt-2 flex flex-wrap gap-1.5">
      {#each data.user.groups as group}
        <span class="badge badge-sm badge-ghost rounded-full text-xs">
          {group}
        </span>
      {:else}
        <span class="text-sm text-base-content/50">No groups</span>
      {/each}
    </div>
  </div>
</section>
