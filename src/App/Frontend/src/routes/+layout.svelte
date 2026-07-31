<script lang="ts">
  import '../app.css';
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Drawer } from '$lib/components/ui';
  import { installApiFetch } from '$lib/api/http';
  import { initTheme } from '$lib/stores/theme';
  import { searchMedia, type SearchHit } from '$lib/api/search';
  import { accentFor, formatDuration, initialsFor } from '$lib/media';
  import {
    Bell,
    ClipboardList,
    Clock,
    Cog,
    Download,
    Heart,
    History,
    House,
    LayoutList,
    LogIn,
    Menu,
    Search,
    Server,
    ShieldCheck,
    User,
    Users,
    X
  } from '@lucide/svelte';

  type IconComponent = typeof House;

  interface NavItem {
    label: string;
    icon: IconComponent;
    href?: string;
    count?: number;
  }

  let { children, data } = $props();

  const primaryNavigation: NavItem[] = [
    { label: 'Home', icon: House, href: '/' }
  ];

  const libraryNavigation: NavItem[] = [
    { label: 'Library', icon: LayoutList, href: '/library' },
    { label: 'Channels', icon: Users, href: '/library/creators' },
    { label: 'History', icon: History, href: '/library?tab=History' },
    { label: 'Watch later', icon: Clock },
    { label: 'Liked', icon: Heart, href: '/library?tab=Liked' }
  ];

  const serverNavigation: NavItem[] = [
    { label: 'Download', icon: Download, href: '/download' },
    { label: 'Jobs', icon: Server, href: '/jobs' },
    { label: 'Playlists', icon: ClipboardList, href: '/playlists' },
    { label: 'Creators', icon: Users, href: '/creators' }
  ];

  const accountNavigation: NavItem[] = [
    { label: 'Profile', icon: User, href: '/profile' },
    { label: 'Settings', icon: Cog }
  ];

  let drawerOpen = $state(false);

  onMount(() => {
    installApiFetch();
    initTheme();
  });

  const closeDrawer = () => {
    drawerOpen = false;
  };

  const isActive = (item: NavItem) =>
    item.href !== undefined &&
    (page.url.pathname === item.href ||
      (item.href !== '/' && item.href !== '/library' && page.url.pathname.startsWith(`${item.href}/`)));

  // Global search
  const SUGGESTION_DEBOUNCE_MS = 250;
  const SUGGESTION_COUNT = 6;

  let searchQuery = $state('');
  let suggestions = $state<SearchHit[]>([]);
  let suggestionsOpen = $state(false);
  let suggestionsLoading = $state(false);
  let suggestionTimer: ReturnType<typeof setTimeout> | null = null;
  let suggestionRequestId = 0;

  $effect(() => {
    // Keep the box in sync with the query while on the results page.
    if (page.url.pathname === '/search') {
      searchQuery = page.url.searchParams.get('q') ?? '';
    }
  });

  function submitSearch(event: SubmitEvent) {
    event.preventDefault();
    const q = searchQuery.trim();
    if (!q) {
      return;
    }
    closeSuggestions();
    void goto(`/search?q=${encodeURIComponent(q)}`);
  }

  function onSearchInput() {
    if (suggestionTimer !== null) {
      clearTimeout(suggestionTimer);
    }
    const q = searchQuery.trim();
    if (q.length < 2) {
      closeSuggestions();
      return;
    }
    suggestionTimer = setTimeout(() => void loadSuggestions(q), SUGGESTION_DEBOUNCE_MS);
  }

  async function loadSuggestions(q: string) {
    const id = ++suggestionRequestId;
    suggestionsLoading = true;
    suggestionsOpen = true;
    try {
      const result = await searchMedia(q, { pageSize: SUGGESTION_COUNT });
      if (id === suggestionRequestId) {
        suggestions = result.items;
      }
    } catch {
      if (id === suggestionRequestId) {
        suggestions = [];
        suggestionsOpen = false;
      }
    } finally {
      if (id === suggestionRequestId) {
        suggestionsLoading = false;
      }
    }
  }

  function closeSuggestions() {
    if (suggestionTimer !== null) {
      clearTimeout(suggestionTimer);
      suggestionTimer = null;
    }
    suggestionRequestId += 1;
    suggestionsOpen = false;
    suggestionsLoading = false;
    suggestions = [];
  }

  function onSearchKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      closeSuggestions();
      (event.currentTarget as HTMLInputElement).blur();
    }
  }

  function onSearchFocusOut(event: FocusEvent) {
    const container = event.currentTarget as HTMLElement;
    if (!(event.relatedTarget instanceof Node) || !container.contains(event.relatedTarget)) {
      closeSuggestions();
    }
  }

  function onGlobalKeydown(event: KeyboardEvent) {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey) {
      return;
    }
    const target = event.target;
    if (
      target instanceof HTMLInputElement ||
      target instanceof HTMLTextAreaElement ||
      (target instanceof HTMLElement && target.isContentEditable)
    ) {
      return;
    }
    event.preventDefault();
    document.getElementById('global-search')?.focus();
  }

  function suggestionThumbnail(hit: SearchHit): string | null {
    return hit.media.thumbnailStoragePath ? `/api/media/watch/${hit.media.mediaGuid}/thumbnail` : null;
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<svelte:window onkeydown={onGlobalKeydown} />

{#snippet navigationGroup(items: NavItem[])}
  <ul class="space-y-1">
    {#each items as item}
      {@const { label, icon: Icon, href, count } = item}
      {@const active = isActive(item)}
      {@const itemClass = [
        'group flex min-h-10 w-full items-center gap-3 rounded-xl px-3 text-left text-sm font-medium transition',
        active
          ? 'bg-primary text-primary-content shadow-sm'
          : 'text-base-content/70 hover:bg-base-200 hover:text-base-content'
      ]}
      <li>
        {#snippet itemContent()}
          <Icon class="h-5 w-5 shrink-0 transition group-hover:text-primary" />
          <span>{label}</span>
          {#if count}
            <span
              class="ml-auto rounded-full bg-base-300 px-2 py-0.5 text-[10px] font-semibold text-base-content/50"
            >
              {count}
            </span>
          {/if}
        {/snippet}
        {#if href}
          <a {href} onclick={closeDrawer} class={itemClass} aria-current={active ? 'page' : undefined}>
            {@render itemContent()}
          </a>
        {:else}
          <button type="button" onclick={closeDrawer} class={itemClass}>
            {@render itemContent()}
          </button>
        {/if}
      </li>
    {/each}
  </ul>
{/snippet}

{#snippet sidebarContent()}
  <div class="flex h-full flex-col">
    <div class="space-y-4 p-2">
      {@render navigationGroup(primaryNavigation)}
      <div class="border-t border-base-300/70 pt-4">
        {@render navigationGroup(libraryNavigation)}
      </div>
      <div class="border-t border-base-300/70 pt-3">
        <p class="mb-2 px-3 text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">
          Server
        </p>
        {@render navigationGroup(serverNavigation)}
      </div>
      <div class="border-t border-base-300/70 pt-3">
        <p class="mb-2 px-3 text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">
          You
        </p>
        {@render navigationGroup(accountNavigation)}
      </div>
    </div>
    <div class="mt-auto border-t border-base-300/70 p-4 text-xs text-base-content/40">
      FrostStream preview
    </div>
  </div>
{/snippet}

<header
  class="fixed inset-x-0 top-0 z-40 flex h-14 items-center border-b border-base-300/70 bg-base-100/95 px-3 backdrop-blur-xl sm:px-5"
>
  <button class="btn btn-sm btn-ghost mr-2 h-10 w-10 lg:hidden" aria-label="Open navigation" onclick={() => (drawerOpen = true)}>
    <Menu class="h-5 w-5" />
  </button>

  <a href="/" class="flex shrink-0 items-center rounded-lg focus-visible:outline-offset-4">
    <img
      src="/froststream-banner.svg"
      alt="FrostStream"
      class="h-8 w-auto max-w-[11.5rem] sm:max-w-[13.5rem]"
      decoding="async"
    />
  </a>

  <div class="relative ml-3 w-full max-w-[39rem] sm:ml-4" onfocusout={onSearchFocusOut}>
    <form role="search" onsubmit={submitSearch}>
      <Search
        class="pointer-events-none absolute left-4 top-1/2 z-10 h-4 w-4 -translate-y-1/2 text-base-content/50"
      />
      <input class="input h-10 w-full rounded-2xl pl-11 pr-11 text-sm sm:pr-20" id="global-search" type="search" autocomplete="off" aria-label="Search your library" placeholder="Search your library, channels, comments..." bind:value={searchQuery} oninput={onSearchInput} onkeydown={onSearchKeydown} />
      <kbd
        class="pointer-events-none absolute right-11 top-1/2 hidden -translate-y-1/2 rounded-md border border-base-content/20 bg-base-300/80 px-2 py-0.5 text-[10px] text-base-content/50 sm:block"
      >
        /
      </kbd>
      <a
        href="/search/advanced"
        aria-label="Advanced search"
        title="Advanced search"
        onclick={closeSuggestions}
        class="absolute right-2 top-1/2 grid h-7 w-7 -translate-y-1/2 place-items-center rounded-lg text-base-content/50 transition hover:bg-base-300/80 hover:text-base-content/80"
      >
        <Cog class="h-4 w-4" />
      </a>
    </form>

    {#if suggestionsOpen}
      <div
        class="absolute inset-x-0 top-full z-50 mt-2 overflow-hidden rounded-2xl border border-base-300 bg-base-200 shadow-2xl shadow-black/40"
        role="listbox"
        aria-label="Search suggestions"
      >
        {#if suggestionsLoading && suggestions.length === 0}
          <div class="flex justify-center p-5">
            <span class="loading loading-spinner loading-sm"></span>
          </div>
        {:else if suggestions.length === 0}
          <p class="p-4 text-sm text-base-content/50">No matches yet — press Enter for a full search.</p>
        {:else}
          <ul>
            {#each suggestions as hit (hit.media.mediaGuid)}
              <li>
                <a
                  href={`/watch/${hit.media.mediaGuid}`}
                  class="flex items-center gap-3 px-3 py-2 transition hover:bg-base-300/70"
                  onclick={closeSuggestions}
                >
                  <span
                    class={`relative grid h-9 w-16 shrink-0 place-items-center overflow-hidden rounded-md bg-gradient-to-br ${accentFor(hit.media.mediaGuid)}`}
                  >
                    <span class="text-[10px] font-black text-white/30">
                      {initialsFor(hit.media.account.accountName)}
                    </span>
                    {#if suggestionThumbnail(hit)}
                      <img
                        src={suggestionThumbnail(hit)}
                        alt=""
                        loading="lazy"
                        decoding="async"
                        class="absolute inset-0 h-full w-full object-cover"
                        onerror={hideBrokenImage}
                      />
                    {/if}
                  </span>
                  <span class="min-w-0">
                    <span class="block truncate text-sm font-medium text-base-content/90">{hit.media.title}</span>
                    <span class="mt-0.5 block truncate text-xs text-base-content/50">
                      {[hit.media.account.accountName, formatDuration(hit.media.durationSeconds)]
                        .filter(Boolean)
                        .join(' · ')}
                    </span>
                  </span>
                </a>
              </li>
            {/each}
          </ul>
          <button
            type="button"
            class="flex w-full items-center gap-2 border-t border-base-300/70 px-4 py-2.5 text-left text-xs font-semibold text-primary transition hover:bg-base-300/70"
            onclick={() => {
              closeSuggestions();
              void goto(`/search?q=${encodeURIComponent(searchQuery.trim())}`);
            }}
          >
            <Search class="h-3.5 w-3.5" />
            See all results for "{searchQuery.trim()}"
          </button>
        {/if}
      </div>
    {/if}
  </div>

  <div class="ml-auto flex shrink-0 items-center gap-1.5 pl-3 sm:gap-2">
    <a class="btn btn-sm btn-primary rounded-full hidden text-xs sm:flex" href="/download">
      <Download class="mr-1.5 h-4 w-4" />
      Download
    </a>
    <button class="btn btn-sm btn-ghost hidden h-10 w-10 sm:flex" aria-label="Notifications">
      <Bell class="h-5 w-5" />
    </button>
    {#if data.user}
      <a class="btn btn-sm btn-ghost h-10 w-10" href="/admin" aria-label="Administration" title="Administration">
        <ShieldCheck class="h-5 w-5" />
      </a>
      <a
        href="/profile"
        aria-label={`Open profile for ${data.user.name}`}
        title={data.user.name}
        class="grid h-9 w-9 place-items-center rounded-full bg-gradient-to-br from-fuchsia-500 to-purple-600 text-xs font-bold text-white ring-2 ring-transparent transition hover:ring-secondary/60 focus-visible:outline-offset-4"
      >
        {data.user.initials}
      </a>
    {:else}
      <a class="btn btn-sm btn-primary rounded-full text-xs" href="/auth/login">
        <LogIn class="mr-1.5 h-4 w-4" />
        Login
      </a>
    {/if}
  </div>
</header>

<aside
  class="fixed bottom-0 left-0 top-14 z-30 hidden w-[232px] overflow-y-auto border-r border-base-300/70 bg-base-100 lg:block"
  aria-label="Primary navigation"
>
  {@render sidebarContent()}
</aside>

<Drawer bind:open={drawerOpen} placement="left" class="w-[min(19rem,88vw)] bg-base-100" label="Mobile navigation">
  <div class="flex h-14 shrink-0 items-center justify-between border-b border-base-300/70 px-4">
    <span class="font-bold text-base-content">Navigation</span>
    <button class="btn btn-sm btn-ghost h-9 w-9" aria-label="Close navigation" onclick={closeDrawer}>
      <X class="h-5 w-5" />
    </button>
  </div>
  <div class="flex-1 overflow-y-auto">
    {@render sidebarContent()}
  </div>
</Drawer>

<main class="min-h-screen pt-14 lg:pl-[232px]">
  <div class="mx-auto max-w-[1560px] px-4 pb-12 pt-5 sm:px-6 sm:pt-7 xl:px-10">
    {@render children()}
  </div>
</main>
