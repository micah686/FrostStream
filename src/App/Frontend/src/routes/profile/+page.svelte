<script lang="ts">
  import { Users } from '@lucide/svelte';

  let { data } = $props();

  const expiresLabel = $derived(data.expiresAt ? new Date(data.expiresAt).toLocaleString() : null);
</script>

<section aria-labelledby="overview-title">
  <div class="mb-5">
    <h2 id="overview-title" class="text-base font-bold text-base-content">Overview</h2>
    <p class="mt-1 text-sm text-base-content/60">Your FrostStream account and current session details.</p>
  </div>

  <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
    <section class="card border border-base-300 bg-base-100 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Display name</p>
      <p class="mt-1 truncate text-sm font-semibold text-base-content">{data.user.name}</p>
    </section>

    <section class="card border border-base-300 bg-base-100 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Username</p>
      <p class="mt-1 truncate text-sm text-base-content/80">{data.user.username ?? data.user.name}</p>
    </section>

    <section class="card border border-base-300 bg-base-100 p-4">
      <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">User GUID</p>
      <p class="mt-1 truncate font-mono text-xs text-base-content/80" title={data.user.subject}>{data.user.subject}</p>
    </section>

    {#if data.user.email}
      <section class="card border border-base-300 bg-base-100 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Email</p>
        <p class="mt-1 truncate text-sm text-base-content/80">{data.user.email}</p>
      </section>
    {/if}

    {#if expiresLabel}
      <section class="card border border-base-300 bg-base-100 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Session expires</p>
        <p class="mt-1 text-sm text-base-content/80">{expiresLabel}</p>
      </section>
    {/if}

    <section class="card border border-base-300 bg-base-100 p-4">
      <p class="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">
        <Users class="h-3.5 w-3.5" />
        Groups
      </p>
      <div class="mt-2 flex flex-wrap gap-1.5">
        {#each data.user.groups as group}
          <span class="badge badge-sm badge-accent text-xs text-accent-content">{group}</span>
        {:else}
          <span class="text-sm text-base-content/50">No groups</span>
        {/each}
      </div>
    </section>
  </div>
</section>
