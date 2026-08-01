<script lang="ts">
  import { page } from '$app/state';
  import { logout } from '$lib/api/http';
  import {
    Bell,
    Cog,
    Cookie,
    FileSearch,
    ListMusic,
    LogOut,
    Palette,
    SlidersHorizontal,
    SlidersVertical,
    User
  } from '@lucide/svelte';

  type IconComponent = typeof User;

  interface ProfileSection {
    label: string;
    icon: IconComponent;
    href: string;
    extra?: string[];
  }

  let { data, children } = $props();

  const authLabel = $derived(data.singleUser ? 'Owner' : 'Signed in');

  const sections: ProfileSection[] = [
    { label: 'Overview', icon: User, href: '/profile' },
    { label: 'Config sets', icon: SlidersHorizontal, href: '/profile/config-sets' },
    { label: 'Option presets', icon: SlidersVertical, href: '/profile/option-presets' },
    { label: 'Cookie management', icon: Cookie, href: '/profile/cookie-management' },
    {
      label: 'Notifications',
      icon: Bell,
      href: '/profile/notifications',
      extra: ['/profile/notification-providers']
    },
    { label: 'Playlists', icon: ListMusic, href: '/profile/playlists' },
    { label: 'Notes', icon: FileSearch, href: '/profile/notes' },
    { label: 'Appearance', icon: Palette, href: '/profile/appearance' }
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
      <h1 id="profile-title" class="text-2xl font-bold tracking-tight text-base-content">Your profile</h1>
      <p class="mt-2 text-sm text-base-content/60">
        {data.user.name} · {authLabel} · Signed in to FrostStream
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      {#if !data.singleUser}
        <button class="btn btn-sm btn-neutral text-xs" onclick={() => void logout()}>
          <LogOut class="mr-1.5 h-4 w-4" />
          Sign out
        </button>
      {/if}
      <button class="btn btn-sm btn-neutral text-xs">
        <Cog class="mr-1.5 h-4 w-4" />
        Edit profile
      </button>
    </div>
  </div>

  <div class="mt-6 grid gap-6 xl:grid-cols-[14rem_minmax(0,1fr)]">
    <aside class="xl:pt-1" aria-label="Profile sections">
      <nav class="menu flex gap-2 overflow-x-auto pb-1 xl:block xl:space-y-2 xl:overflow-visible xl:pb-0">
        {#each sections as section}
          {@const { label, icon: Icon, href } = section}
          {@const active = isActive(section)}
          <a
            {href}
            class={[
              'flex h-10 shrink-0 items-center gap-3 rounded-lg px-4 text-sm font-medium transition xl:w-full',
              active
                ? 'menu-active bg-primary text-primary-content shadow-sm'
                : 'text-base-content/70 hover:bg-base-200 hover:text-base-content'
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
