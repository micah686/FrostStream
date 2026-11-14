<script lang="ts">
  import { page } from '$app/state';
  import { Clock, Download, Video } from '@lucide/svelte';

  type IconComponent = typeof Download;

  interface JobsSection {
    label: string;
    icon: IconComponent;
    href: string;
  }

  let { children } = $props();

  const sections: JobsSection[] = [
    { label: 'Downloads', icon: Download, href: '/jobs' },
    { label: 'Encoding', icon: Video, href: '/jobs/encoding' },
    { label: 'Background', icon: Clock, href: '/jobs/background' }
  ];

  function isActive(section: JobsSection): boolean {
    const path = page.url.pathname;
    if (section.href === '/jobs') {
      return path === '/jobs';
    }
    return path === section.href || path.startsWith(`${section.href}/`);
  }
</script>

<svelte:head>
  <title>Jobs · FrostStream</title>
</svelte:head>

<section class="min-h-[calc(100vh-7rem)]" aria-labelledby="jobs-title">
  <div class="min-w-0">
    <h1 id="jobs-title" class="text-2xl font-bold tracking-tight text-base-content">Jobs</h1>
    <p class="mt-2 text-sm text-base-content/60">Downloads, encoding, and background work</p>
  </div>

  <div class="mt-5 space-y-5">
    <aside class="border-b border-base-300" aria-label="Jobs sections">
      <nav class="tabs tabs-border flex-nowrap overflow-x-auto" aria-label="Job categories">
        {#each sections as section}
          {@const { label, icon: Icon, href } = section}
          {@const active = isActive(section)}
          <a
            {href}
            class={[
              'tab flex h-11 shrink-0 items-center gap-2 px-4 text-sm font-medium',
              active
                ? 'tab-active text-primary'
                : 'text-base-content/60 hover:text-base-content'
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
