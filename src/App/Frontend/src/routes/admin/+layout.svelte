<script lang="ts">
  import { page } from '$app/state';
  import {
    ApiKeyOutline,
    ChartMixedOutline,
    ClockOutline,
    CloudArrowUpOutline,
    DatabaseOutline,
    FileImportOutline,
    ServerOutline,
    TagOutline
  } from 'flowbite-svelte-icons';

  type IconComponent = typeof DatabaseOutline;

  interface AdminSection {
    label: string;
    icon: IconComponent;
    href: string;
  }

  let { children, data } = $props();

  const allSections: AdminSection[] = [
    { label: 'Storage', icon: DatabaseOutline, href: '/admin/storage' },
    { label: 'Statistics', icon: ChartMixedOutline, href: '/admin/statistics' },
    { label: 'Metadata', icon: TagOutline, href: '/admin/metadata' },
    { label: 'Import', icon: FileImportOutline, href: '/admin/import' },
    { label: 'Workers', icon: ServerOutline, href: '/admin/workers' },
    { label: 'Access control', icon: ApiKeyOutline, href: '/admin/access-control' },
    { label: 'Backups', icon: CloudArrowUpOutline, href: '/admin/backups' },
    { label: 'Schedules', icon: ClockOutline, href: '/admin/schedules' }
  ];

  const sections = $derived(
    data.singleUser
      ? allSections.filter((section) => section.href !== '/admin/access-control')
      : allSections
  );

  const isActive = (href: string) =>
    page.url.pathname === href || page.url.pathname.startsWith(`${href}/`);
</script>

<svelte:head>
  <title>Administration · FrostStream</title>
</svelte:head>

<section class="min-h-[calc(100vh-7rem)]" aria-labelledby="admin-title">
  <div class="min-w-0">
    <h1 id="admin-title" class="text-2xl font-bold tracking-tight text-base-content">Administration</h1>
    <p class="mt-2 text-sm text-base-content/60">Server-wide settings · requires Owner</p>
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
                ? 'bg-primary/18 text-primary'
                : 'text-base-content/60 hover:bg-base-300/70 hover:text-base-content'
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
