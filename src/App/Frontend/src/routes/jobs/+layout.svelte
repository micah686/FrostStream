<script lang="ts">
  import { page } from '$app/state';
  import { ClockOutline, DownloadOutline, VideoCameraOutline } from 'flowbite-svelte-icons';

  type IconComponent = typeof DownloadOutline;

  interface JobsSection {
    label: string;
    icon: IconComponent;
    href: string;
  }

  let { children } = $props();

  const sections: JobsSection[] = [
    { label: 'Downloads', icon: DownloadOutline, href: '/jobs' },
    { label: 'Encoding', icon: VideoCameraOutline, href: '/jobs/encoding' },
    { label: 'Background', icon: ClockOutline, href: '/jobs/background' }
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
    <h1 id="jobs-title" class="text-2xl font-bold tracking-tight text-slate-100">Jobs</h1>
    <p class="mt-2 text-sm text-slate-400">Downloads, encoding, and background work</p>
  </div>

  <div class="mt-6 grid gap-6 xl:grid-cols-[14rem_minmax(0,1fr)]">
    <aside class="xl:pt-1" aria-label="Jobs sections">
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
