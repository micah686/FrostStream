<script lang="ts">
  import { Badge, Button } from 'flowbite-svelte';
  import { ArrowRightToBracketOutline, UsersGroupOutline } from 'flowbite-svelte-icons';

  let { data } = $props();

  const expiresLabel = $derived(
    data.expiresAt ? new Date(data.expiresAt).toLocaleString() : null
  );
</script>

<svelte:head>
  <title>Profile · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-3xl" aria-labelledby="profile-title">
  <div
    class="relative overflow-hidden rounded-2xl border border-slate-800/80 bg-slate-900/40 shadow-2xl shadow-black/20"
  >
    <div class="h-28 bg-[linear-gradient(115deg,#1b3154_0%,#203f75_44%,#271534_100%)]"></div>
    <div class="px-6 pb-6 sm:px-8 sm:pb-8">
      <div class="-mt-10 flex flex-wrap items-end justify-between gap-4">
        <span
          class="grid h-20 w-20 place-items-center rounded-full bg-gradient-to-br from-fuchsia-500 to-purple-600 text-2xl font-black text-white ring-4 ring-[#0d1017]"
        >
          {data.user.initials}
        </span>
        {#if !data.singleUser}
          <Button
            href="/auth/logout"
            color="dark"
            class="border-slate-700! bg-slate-800/80! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-700!"
          >
            <ArrowRightToBracketOutline class="mr-1.5 h-4 w-4" />
            Sign out
          </Button>
        {/if}
      </div>

      <div class="mt-4">
        <div class="flex flex-wrap items-center gap-2">
          <h1 id="profile-title" class="text-2xl font-bold tracking-tight text-white">
            {data.user.name}
          </h1>
          <Badge
            rounded
            color={data.singleUser ? 'green' : 'blue'}
            class="px-2.5! py-1! text-[10px]! font-bold! tracking-wide!"
          >
            {data.singleUser ? 'SINGLE USER' : 'AUTHENTIK'}
          </Badge>
        </div>
        {#if data.user.username}
          <p class="mt-1 text-sm text-slate-500">@{data.user.username}</p>
        {/if}
      </div>

      <dl class="mt-6 grid gap-4 sm:grid-cols-2">
        {#if data.user.email}
          <div class="rounded-xl border border-slate-800/80 bg-slate-900/60 p-4">
            <dt class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Email</dt>
            <dd class="mt-1 truncate text-sm text-slate-200">{data.user.email}</dd>
          </div>
        {/if}
        {#if data.user.subject}
          <div class="rounded-xl border border-slate-800/80 bg-slate-900/60 p-4">
            <dt class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
              Subject
            </dt>
            <dd class="mt-1 truncate font-mono text-sm text-slate-200">{data.user.subject}</dd>
          </div>
        {/if}
        {#if expiresLabel}
          <div class="rounded-xl border border-slate-800/80 bg-slate-900/60 p-4">
            <dt class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
              Session expires
            </dt>
            <dd class="mt-1 text-sm text-slate-200">{expiresLabel}</dd>
          </div>
        {/if}
        <div class="rounded-xl border border-slate-800/80 bg-slate-900/60 p-4">
          <dt
            class="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600"
          >
            <UsersGroupOutline class="h-3.5 w-3.5" />
            Groups
          </dt>
          <dd class="mt-2 flex flex-wrap gap-1.5">
            {#each data.user.groups as group}
              <Badge rounded color="gray" class="bg-slate-800! px-2.5! py-0.5! text-xs! text-slate-300!">
                {group}
              </Badge>
            {:else}
              <span class="text-sm text-slate-500">No groups on this account</span>
            {/each}
          </dd>
        </div>
      </dl>
    </div>
  </div>
</section>
