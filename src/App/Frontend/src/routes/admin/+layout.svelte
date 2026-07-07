<script lang="ts">
  import { page } from '$app/state';
  import {
    ApiKeyOutline,
    ChartMixedOutline,
    ClockOutline,
    CloudArrowUpOutline,
    CubesStackedOutline,
    DatabaseOutline,
    FileImportOutline,
    TagOutline
  } from 'flowbite-svelte-icons';

  type IconComponent = typeof DatabaseOutline;

  interface AdminSection {
    label: string;
    icon: IconComponent;
    href: string;
  }

  let { children } = $props();

  const sections: AdminSection[] = [
    { label: 'Storage', icon: DatabaseOutline, href: '/admin/storage' },
    { label: 'Statistics', icon: ChartMixedOutline, href: '/admin/statistics' },
    { label: 'Metadata', icon: TagOutline, href: '/admin/metadata' },
    { label: 'Import', icon: FileImportOutline, href: '/admin/import' },
    { label: 'Media access', icon: ApiKeyOutline, href: '/admin/media-access' },
    { label: 'Bundle management', icon: CubesStackedOutline, href: '/admin/bundle-management' },
    { label: 'Backups', icon: CloudArrowUpOutline, href: '/admin/backups' },
    { label: 'Schedules', icon: ClockOutline, href: '/admin/schedules' }
  ];

  const isActive = (href: string) =>
    page.url.pathname === href || page.url.pathname.startsWith(`${href}/`);
</script>

<svelte:head>
  <title>Administration · FrostStream</title>
</svelte:head>

<section class="min-h-[calc(100vh-7rem)]" aria-labelledby="admin-title">
  <div class="min-w-0">
    <h1 id="admin-title" class="text-2xl font-bold tracking-tight text-slate-100">Administration</h1>
    <p class="mt-2 text-sm text-slate-400">Server-wide settings · requires Owner</p>
  </div>

  <div class="mt-6 grid gap-6 xl:grid-cols-[16rem_minmax(0,1fr)]">
    <aside class="xl:pt-1" aria-label="Administration sections">
      <nav class="flex gap-2 overflow-x-auto pb-1 xl:block xl:space-y-2 xl:overflow-visible xl:pb-0">
        {#each sections as section}
          {@const { label, icon: Icon, href } = section}
          {@const active = isActive(href)}
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
            <span class="whitespace-nowrap">{label}</span>
          </a>
        {/each}
      </nav>
    </aside>

    <div class="min-w-0 space-y-5">
      {@render children()}
    </div>
  </div>
</section>
