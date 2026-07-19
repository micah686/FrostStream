<script lang="ts">
  import { page } from '$app/state';
  import { logout } from '$lib/api/http';
  import { Button } from 'flowbite-svelte';
  import {
    AdjustmentsHorizontalOutline,
    AdjustmentsVerticalOutline,
    ArrowRightToBracketOutline,
    BellOutline,
    CogOutline,
    CookieSolid,
    FileSearchOutline,
    ListMusicOutline,
    UserOutline
  } from 'flowbite-svelte-icons';

  type IconComponent = typeof UserOutline;

  interface ProfileSection {
    label: string;
    icon: IconComponent;
    href: string;
    extra?: string[];
  }

  let { data, children } = $props();

  const authLabel = $derived(data.singleUser ? 'Owner' : 'Signed in');

  const sections: ProfileSection[] = [
    { label: 'Overview', icon: UserOutline, href: '/profile' },
    { label: 'Config sets', icon: AdjustmentsHorizontalOutline, href: '/profile/config-sets' },
    { label: 'Option presets', icon: AdjustmentsVerticalOutline, href: '/profile/option-presets' },
    { label: 'Cookie management', icon: CookieSolid, href: '/profile/cookie-management' },
    {
      label: 'Notifications',
      icon: BellOutline,
      href: '/profile/notifications',
      extra: ['/profile/notification-providers']
    },
    { label: 'Playlists', icon: ListMusicOutline, href: '/profile/playlists' },
    { label: 'Notes', icon: FileSearchOutline, href: '/profile/notes' }
  ];

  function isActive(section: ProfileSection): boolean {
    const path = page.url.pathname;
    if (section.href === '/profile') {
      return path === '/profile';
    }
    if (path === section.href || path.startsWith(`${section.href}/`)) {
      return true;
    }
    return (section.extra ?? []).some((prefix) => path === prefix || path.startsWith(`${prefix}/`));
  }
</script>

<svelte:head>
  <title>Profile · FrostStream</title>
</svelte:head>

<section class="min-h-[calc(100vh-7rem)]" aria-labelledby="profile-title">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-start sm:justify-between">
    <div class="min-w-0">
      <h1 id="profile-title" class="text-2xl font-bold tracking-tight text-slate-100">Your profile</h1>
      <p class="mt-2 text-sm text-slate-400">
        {data.user.name} · {authLabel} · Signed in to FrostStream
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      {#if !data.singleUser}
        <Button
          onclick={() => void logout()}
          color="dark"
          class="border-slate-700! bg-[#0f1420]! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
        >
          <ArrowRightToBracketOutline class="mr-1.5 h-4 w-4" />
          Sign out
        </Button>
      {/if}
      <Button
        color="dark"
        class="border-slate-700! bg-[#0f1420]! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
      >
        <CogOutline class="mr-1.5 h-4 w-4" />
        Edit profile
      </Button>
    </div>
  </div>

  <div class="mt-6 grid gap-6 xl:grid-cols-[14rem_minmax(0,1fr)]">
    <aside class="xl:pt-1" aria-label="Profile sections">
      <nav class="flex gap-2 overflow-x-auto pb-1 xl:block xl:space-y-2 xl:overflow-visible xl:pb-0">
        {#each sections as section}
          {@const { label, icon: Icon, href } = section}
          {@const active = isActive(section)}
          <a
            {href}
            class={[
              'flex h-10 shrink-0 items-center gap-3 rounded-lg px-4 text-sm font-medium transition xl:w-full',
              active
                ? 'bg-blue-500/18 text-blue-400'
                : 'text-slate-400 hover:bg-slate-800/70 hover:text-slate-100'
            ]}
            aria-current={active ? 'page' : undefined}
          >
            <Icon class="h-4.5 w-4.5 shrink-0" />
            <span>{label}</span>
          </a>
        {/each}
      </nav>
    </aside>

    <div class="min-w-0 space-y-5">
      {@render children()}
    </div>
  </div>
</section>
