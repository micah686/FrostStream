<script lang="ts">
  import { Badge, Button, Drawer, Input } from 'flowbite-svelte';
  import {
    BarsOutline,
    BookmarkOutline,
    CameraPhotoOutline,
    ChevronRightOutline,
    ClockArrowOutline,
    ClockOutline,
    CloseOutline,
    CogOutline,
    DownloadOutline,
    FireOutline,
    GlobeOutline,
    HeartOutline,
    HomeOutline,
    PlaySolid,
    RectangleListOutline,
    SearchOutline,
    ServerOutline,
    UserOutline,
    VideoCameraOutline
  } from 'flowbite-svelte-icons';

  type IconComponent = typeof HomeOutline;

  interface NavItem {
    label: string;
    icon: IconComponent;
    active?: boolean;
    count?: number;
  }

  interface Subscription {
    name: string;
    initials: string;
    color: string;
    live?: boolean;
  }

  interface MediaCard {
    title: string;
    creator: string;
    duration: string;
    progress: number;
    artwork: 'mountain' | 'spectrum' | 'circles' | 'horizon';
    accent: string;
    initials: string;
  }

  const primaryNavigation: NavItem[] = [
    { label: 'Home', icon: HomeOutline, active: true },
    { label: 'Explore', icon: GlobeOutline },
    { label: 'Trending', icon: FireOutline }
  ];

  const libraryNavigation: NavItem[] = [
    { label: 'Library', icon: RectangleListOutline },
    { label: 'History', icon: ClockArrowOutline },
    { label: 'Watch later', icon: ClockOutline },
    { label: 'Liked', icon: HeartOutline }
  ];

  const serverNavigation: NavItem[] = [
    { label: 'Download', icon: DownloadOutline },
    { label: 'Jobs', icon: ServerOutline, count: 4 }
  ];

  const accountNavigation: NavItem[] = [
    { label: 'Profile', icon: UserOutline },
    { label: 'Settings', icon: CogOutline }
  ];

  const categories: string[] = [
    'All',
    'Continue watching',
    'Subscriptions',
    'Music',
    'Field recordings',
    'Tech',
    'Photography',
    'Pixel art',
    'Long-form',
    'Live'
  ];

  const subscriptions: Subscription[] = [
    { name: 'Miles Lab', initials: 'ML', color: 'from-indigo-500 to-violet-500' },
    { name: 'Darkroom Diaries', initials: 'DD', color: 'from-rose-500 to-orange-500', live: true },
    { name: 'Fieldnotes', initials: 'FN', color: 'from-emerald-400 to-teal-600' }
  ];

  const media: MediaCard[] = [
    {
      title: 'A quiet morning above the fog',
      creator: 'Miles Lab',
      duration: '18:42',
      progress: 38,
      artwork: 'mountain',
      accent: 'from-slate-800 to-blue-950',
      initials: 'ML'
    },
    {
      title: 'Designing a warmer synth patch',
      creator: 'Neon Oscillator',
      duration: '24:08',
      progress: 62,
      artwork: 'spectrum',
      accent: 'from-purple-950 to-violet-700',
      initials: 'NO'
    },
    {
      title: 'Golden hour portraits on film',
      creator: 'Grain & Velvet',
      duration: '12:16',
      progress: 21,
      artwork: 'circles',
      accent: 'from-red-950 to-orange-900',
      initials: 'GV'
    },
    {
      title: 'Building a distraction-free desk',
      creator: 'Miles Lab',
      duration: '31:05',
      progress: 76,
      artwork: 'horizon',
      accent: 'from-blue-950 to-slate-800',
      initials: 'ML'
    }
  ];

  let drawerOpen = $state(false);

  const closeDrawer = () => {
    drawerOpen = false;
  };
</script>

<svelte:head>
  <title>FrostStream</title>
  <meta
    name="description"
    content="A dark media dashboard shell for browsing a personal FrostStream library."
  />
</svelte:head>

{#snippet navigationGroup(items: NavItem[])}
  <ul class="space-y-1">
    {#each items as { label, icon: Icon, active, count }}
      <li>
        <button
          type="button"
          onclick={closeDrawer}
          class={[
            'group flex min-h-10 w-full items-center gap-3 rounded-xl px-3 text-left text-sm font-medium transition',
            active
              ? 'bg-blue-500/18 text-blue-400'
              : 'text-slate-400 hover:bg-slate-800/70 hover:text-slate-100'
          ]}
          aria-current={active ? 'page' : undefined}
        >
          <Icon class="h-5 w-5 shrink-0 transition group-hover:text-blue-400" />
          <span>{label}</span>
          {#if count}
            <span
              class="ml-auto rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-500"
            >
              {count}
            </span>
          {/if}
        </button>
      </li>
    {/each}
  </ul>
{/snippet}

{#snippet sidebarContent()}
  <div class="flex h-full flex-col">
    <div class="space-y-4 p-2">
      {@render navigationGroup(primaryNavigation)}
      <div class="border-t border-slate-800/70 pt-4">
        {@render navigationGroup(libraryNavigation)}
      </div>
      <div class="border-t border-slate-800/70 pt-3">
        <p class="mb-2 px-3 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
          Server
        </p>
        {@render navigationGroup(serverNavigation)}
      </div>
      <div class="border-t border-slate-800/70 pt-3">
        <p class="mb-2 px-3 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
          You
        </p>
        {@render navigationGroup(accountNavigation)}
      </div>
      <div class="border-t border-slate-800/70 pt-3">
        <p class="mb-2 px-3 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
          Subscriptions
        </p>
        <ul class="space-y-1">
          {#each subscriptions as subscription}
            <li>
              <button
                type="button"
                onclick={closeDrawer}
                class="flex min-h-9 w-full items-center gap-3 rounded-xl px-3 text-left text-sm text-slate-400 transition hover:bg-slate-800/70 hover:text-white"
              >
                <span
                  class={`grid h-6 w-6 shrink-0 place-items-center rounded-full bg-gradient-to-br ${subscription.color} text-[9px] font-bold text-white`}
                >
                  {subscription.initials}
                </span>
                <span class="truncate">{subscription.name}</span>
                {#if subscription.live}
                  <span class="ml-auto h-2 w-2 rounded-full bg-red-500 ring-4 ring-red-500/10">
                    <span class="sr-only">Live now</span>
                  </span>
                {/if}
              </button>
            </li>
          {/each}
        </ul>
      </div>
    </div>
    <div class="mt-auto border-t border-slate-800/70 p-4 text-xs text-slate-600">
      FrostStream preview
    </div>
  </div>
{/snippet}

<header
  class="fixed inset-x-0 top-0 z-40 flex h-14 items-center border-b border-slate-800/70 bg-[#0d1017]/95 px-3 backdrop-blur-xl sm:px-5"
>
  <Button
    color="dark"
    class="mr-2 h-10 w-10 border-0! bg-transparent! p-2.5! text-slate-400 hover:bg-slate-800! hover:text-white! lg:hidden"
    aria-label="Open navigation"
    onclick={() => (drawerOpen = true)}
  >
    <BarsOutline class="h-5 w-5" />
  </Button>

  <a href="/" class="flex shrink-0 items-center gap-2.5 rounded-lg focus-visible:outline-offset-4">
    <span
      class="grid h-8 w-8 place-items-center rounded-lg bg-gradient-to-br from-blue-400 to-blue-700 shadow-[0_0_18px_rgba(59,130,246,0.4)]"
    >
      <PlaySolid class="h-4 w-4 text-white" />
    </span>
    <span class="hidden text-base font-bold tracking-tight text-slate-100 sm:block">FrostStream</span>
  </a>

  <div class="relative ml-3 w-full max-w-[39rem] sm:ml-4">
    <SearchOutline
      class="pointer-events-none absolute left-4 top-1/2 z-10 h-4 w-4 -translate-y-1/2 text-slate-500"
    />
    <Input
      type="search"
      aria-label="Search your library"
      placeholder="Search your library, channels, comments..."
      class="h-10 rounded-2xl! border-slate-800! bg-slate-900/80! py-2! pl-11! pr-11! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
    />
    <kbd
      class="pointer-events-none absolute right-3 top-1/2 hidden -translate-y-1/2 rounded-md border border-slate-700/70 bg-slate-800/80 px-2 py-0.5 text-[10px] text-slate-500 sm:block"
    >
      /
    </kbd>
  </div>

  <Button
    color="dark"
    class="ml-auto hidden h-10 w-10 border-0! bg-transparent! p-2.5! text-slate-400 hover:bg-slate-800! hover:text-white! sm:flex"
    aria-label="Open camera tools"
  >
    <VideoCameraOutline class="h-5 w-5" />
  </Button>
</header>

<aside
  class="fixed bottom-0 left-0 top-14 z-30 hidden w-[232px] overflow-y-auto border-r border-slate-800/70 bg-[#0d1017] lg:block"
  aria-label="Primary navigation"
>
  {@render sidebarContent()}
</aside>

<Drawer
  bind:open={drawerOpen}
  placement="left"
  class="w-[min(19rem,88vw)]! border-r! border-slate-800! bg-[#0d1017]! p-0!"
  aria-label="Mobile navigation"
>
  <div class="flex h-14 items-center justify-between border-b border-slate-800/70 px-4">
    <span class="font-bold text-white">Navigation</span>
    <Button
      color="dark"
      class="h-9 w-9 border-0! bg-transparent! p-2! text-slate-400 hover:bg-slate-800! hover:text-white!"
      aria-label="Close navigation"
      onclick={closeDrawer}
    >
      <CloseOutline class="h-5 w-5" />
    </Button>
  </div>
  <div class="h-[calc(100vh-3.5rem)] overflow-y-auto">
    {@render sidebarContent()}
  </div>
</Drawer>

<main class="min-h-screen pt-14 lg:pl-[232px]">
  <div class="mx-auto max-w-[1560px] px-4 pb-12 pt-5 sm:px-6 sm:pt-7 xl:px-10">
    <section
      class="relative overflow-hidden rounded-2xl border border-blue-400/5 bg-[linear-gradient(115deg,#1b3154_0%,#203f75_44%,#271534_100%)] shadow-2xl shadow-black/20"
      aria-labelledby="featured-title"
    >
      <div
        class="pointer-events-none absolute inset-0 opacity-20 [background-image:linear-gradient(rgba(255,255,255,.04)_1px,transparent_1px),linear-gradient(90deg,rgba(255,255,255,.04)_1px,transparent_1px)] [background-size:4px_4px]"
      ></div>
      <div class="relative grid min-h-[360px] gap-8 p-7 sm:p-10 xl:grid-cols-[1.1fr_1fr] xl:p-10">
        <div class="flex max-w-2xl flex-col justify-center">
          <div class="mb-5 flex items-center gap-2">
            <Badge
              rounded
              color="blue"
              class="bg-blue-300/12! px-3! py-1.5! text-[10px]! font-bold! tracking-[0.08em]! text-blue-100!"
            >
              FEATURED
            </Badge>
            <span class="text-[11px] font-semibold tracking-wide text-blue-200/80">PIXELFORGE</span>
          </div>
          <h1
            id="featured-title"
            class="max-w-xl text-4xl font-black leading-[1.08] tracking-[-0.04em] text-white sm:text-5xl"
          >
            CRT pixel art workflow without the eye strain
          </h1>
          <p class="mt-4 max-w-xl text-sm leading-6 text-blue-100/75 sm:text-base">
            A patient 17-minute walkthrough of the workflow used to make all the pixel art on this
            server. CRT-friendly palettes, no eye strain, no hype, just small decisions that compound.
          </p>
          <div class="mt-6 flex flex-wrap gap-3">
            <Button
              color="blue"
              class="border-0! bg-blue-500! px-5! py-2.5! font-semibold! shadow-lg shadow-blue-950/30 hover:bg-blue-400!"
            >
              <PlaySolid class="mr-2 h-4 w-4" />
              Watch now
            </Button>
            <Button
              color="dark"
              class="border-blue-200/10! bg-white/6! px-5! py-2.5! font-semibold! text-blue-50! hover:bg-white/12!"
            >
              <BookmarkOutline class="mr-2 h-4 w-4" />
              Watch later
            </Button>
          </div>
        </div>

        <div
          class="relative hidden min-h-[280px] overflow-hidden rounded-2xl border border-lime-300/5 bg-[#294d0f] shadow-xl shadow-black/20 sm:block"
          aria-label="Featured media preview placeholder"
        >
          <Badge
            color="blue"
            class="absolute left-3 top-3 z-10 rounded-md! px-2! py-1! text-[10px]! font-bold!"
          >
            NEW
          </Badge>
          <div
            class="absolute inset-[15%_12%] overflow-hidden rounded-xl bg-lime-100/[0.035] shadow-inner shadow-black/10"
          >
            <div class="absolute inset-x-0 top-1/3 border-t border-lime-100/10"></div>
            <div class="absolute inset-x-0 top-2/3 border-t border-lime-100/10"></div>
            <button
              type="button"
              aria-label="Play featured media"
              class="absolute left-1/2 top-1/2 grid h-16 w-16 -translate-x-1/2 -translate-y-1/2 place-items-center rounded-full bg-white text-[#11151c] shadow-xl transition hover:scale-105 hover:bg-blue-50"
            >
              <PlaySolid class="ml-1 h-7 w-7" />
            </button>
          </div>
          <span
            class="absolute bottom-3 right-3 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-semibold text-white"
          >
            17:32
          </span>
        </div>
      </div>
    </section>

    <nav class="mt-7 flex gap-2 overflow-x-auto pb-2" aria-label="Media categories">
      {#each categories as category, index}
        <Button
          pill
          color={index === 0 ? 'light' : 'dark'}
          size="xs"
          class={[
            'shrink-0 border-0! px-4! py-2! text-xs! font-medium!',
            index === 0
              ? 'bg-slate-100! text-slate-900! hover:bg-white!'
              : 'bg-slate-800/80! text-slate-300! hover:bg-slate-700!'
          ]}
        >
          {category}
        </Button>
      {/each}
    </nav>

    <section class="mt-5" aria-labelledby="continue-watching-title">
      <div class="mb-4 flex items-end justify-between gap-4">
        <div>
          <h2 id="continue-watching-title" class="text-xl font-bold tracking-tight text-slate-100">
            Continue watching
          </h2>
          <p class="mt-1 text-sm text-slate-500">Pick up where you left off across devices</p>
        </div>
        <button
          type="button"
          class="hidden items-center gap-1 rounded-lg px-2 py-1 text-xs font-semibold text-slate-500 transition hover:text-blue-400 sm:flex"
        >
          See all
          <ChevronRightOutline class="h-3 w-3" />
        </button>
      </div>

      <div class="grid gap-5 sm:grid-cols-2 xl:grid-cols-4">
        {#each media as item}
          <article class="group min-w-0">
            <button
              type="button"
              class={`relative block aspect-video w-full overflow-hidden rounded-2xl bg-gradient-to-br ${item.accent} text-left shadow-lg shadow-black/20 transition duration-300 group-hover:-translate-y-1 group-hover:shadow-xl group-hover:shadow-black/30`}
              aria-label={`Resume ${item.title}`}
            >
              {#if item.artwork === 'mountain'}
                <div
                  class="absolute bottom-0 left-[10%] h-[74%] w-[38%] bg-slate-600/25 [clip-path:polygon(50%_0,100%_100%,0_100%)]"
                ></div>
                <div
                  class="absolute bottom-0 right-[8%] h-[55%] w-[44%] bg-slate-500/20 [clip-path:polygon(50%_0,100%_100%,0_100%)]"
                ></div>
              {:else if item.artwork === 'spectrum'}
                <div class="absolute inset-x-6 bottom-6 flex h-[55%] items-end justify-center gap-2 opacity-30">
                  {#each [45, 72, 38, 84, 51, 68, 35, 77, 48, 62] as height}
                    <span class="w-full rounded-t bg-fuchsia-300" style={`height: ${height}%`}></span>
                  {/each}
                </div>
              {:else if item.artwork === 'circles'}
                <div class="absolute -right-5 -top-8 h-36 w-36 rounded-full bg-orange-100/10"></div>
                <div class="absolute right-10 top-7 h-20 w-20 rounded-full bg-orange-100/8"></div>
              {:else}
                <div
                  class="absolute inset-x-0 bottom-0 h-1/2 bg-[linear-gradient(155deg,transparent_42%,rgba(96,165,250,.14)_43%,rgba(96,165,250,.06)_100%)]"
                ></div>
                <div class="absolute inset-x-7 top-1/3 h-px bg-blue-200/10"></div>
              {/if}

              <span
                class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 text-3xl font-black text-white/15"
              >
                {item.initials}
              </span>
              <span class="absolute right-5 top-5 h-7 w-7 rounded-full bg-white/15"></span>
              <span
                class="absolute bottom-3 right-3 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-semibold text-white"
              >
                {item.duration}
              </span>
              <span class="absolute inset-x-0 bottom-0 h-1 bg-black/35">
                <span class="block h-full bg-blue-500" style={`width: ${item.progress}%`}></span>
              </span>
              <span
                class="absolute left-1/2 top-1/2 grid h-12 w-12 -translate-x-1/2 -translate-y-1/2 scale-90 place-items-center rounded-full bg-white/95 text-slate-950 opacity-0 shadow-xl transition duration-200 group-hover:scale-100 group-hover:opacity-100"
              >
                <PlaySolid class="ml-0.5 h-5 w-5" />
              </span>
            </button>
            <div class="mt-3 min-w-0 px-1">
              <h3 class="truncate text-sm font-semibold text-slate-200">{item.title}</h3>
              <p class="mt-1 truncate text-xs text-slate-500">{item.creator}</p>
            </div>
          </article>
        {/each}
      </div>
    </section>

    <section class="mt-10 grid gap-4 md:grid-cols-3" aria-label="Library overview">
      <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
        <CameraPhotoOutline class="h-5 w-5 text-blue-400" />
        <p class="mt-4 text-2xl font-bold text-white">248</p>
        <p class="mt-1 text-xs text-slate-500">Videos in your library</p>
      </div>
      <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
        <GlobeOutline class="h-5 w-5 text-violet-400" />
        <p class="mt-4 text-2xl font-bold text-white">12</p>
        <p class="mt-1 text-xs text-slate-500">Channels followed</p>
      </div>
      <div class="rounded-2xl border border-slate-800/80 bg-slate-900/40 p-5">
        <ClockOutline class="h-5 w-5 text-emerald-400" />
        <p class="mt-4 text-2xl font-bold text-white">36h</p>
        <p class="mt-1 text-xs text-slate-500">Ready to watch offline</p>
      </div>
    </section>
  </div>
</main>
