<script lang="ts">
  import { onMount } from 'svelte';
  import {
    ChevronRight,
    CircleAlert,
    ExternalLink,
    RefreshCw,
    Search,
    Users
  } from '@lucide/svelte';
  import { listAccounts, type AccountSummary } from '$lib/api/accounts';
  import { accentFor, formatCount, initialsFor } from '$lib/media';

  const pageSize = 36;

  let accounts = $state<AccountSummary[]>([]);
  let cursorStack = $state<string[]>([]);
  let nextCursor = $state<string | null>(null);
  let hasMore = $state(false);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let platformFilter = $state('');
  let submittedPlatform = $state('');
  let pageNumber = $state(1);

  const platformSummary = $derived(
    [...new Set(accounts.map((account) => account.platform).filter(Boolean))].sort((a, b) => a.localeCompare(b))
  );

  onMount(() => {
    void loadPage(null, 1, []);
  });

  async function loadPage(after: string | null, targetPage: number, stack: string[]) {
    loading = true;
    loadError = null;
    try {
      const response = await listAccounts({
        pageSize,
        after,
        platform: submittedPlatform || null
      });
      accounts = response.items;
      nextCursor = response.nextCursor;
      hasMore = response.hasMore;
      pageNumber = targetPage;
      cursorStack = stack;
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load creator accounts.';
    } finally {
      loading = false;
    }
  }

  function submitFilters(event: SubmitEvent) {
    event.preventDefault();
    submittedPlatform = platformFilter.trim();
    void loadPage(null, 1, []);
  }

  function clearFilters() {
    platformFilter = '';
    submittedPlatform = '';
    void loadPage(null, 1, []);
  }

  function nextPage() {
    if (!nextCursor) {
      return;
    }
    const currentCursor = cursorStack.at(-1) ?? '';
    void loadPage(nextCursor, pageNumber + 1, [...cursorStack, currentCursor]);
  }

  function previousPage() {
    if (pageNumber <= 1) {
      return;
    }
    const nextStack = cursorStack.slice(0, -1);
    const previousCursor = nextStack.at(-1) || null;
    void loadPage(previousCursor, pageNumber - 1, nextStack);
  }

  function accountMeta(account: AccountSummary): string {
    return [
      formatCount(account.followerCount) ? `${formatCount(account.followerCount)} followers` : null,
      `${account.mediaCount.toLocaleString()} ${account.mediaCount === 1 ? 'video' : 'videos'}`
    ]
      .filter(Boolean)
      .join(' · ');
  }

  function hideBrokenImage(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<svelte:head>
  <title>Creators · Library · FrostStream</title>
</svelte:head>

<section aria-labelledby="library-creators-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="library-creators-title" class="text-2xl font-bold tracking-tight text-base-content">Creators</h1>
      <p class="mt-1 text-sm text-base-content/50">
        Creator accounts discovered from archived metadata.
      </p>
    </div>
    <button class="btn btn-sm btn-neutral text-xs" disabled={loading} onclick={() => loadPage(null, 1, [])}>
      {#if loading}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <RefreshCw class="mr-1.5 h-4 w-4" />
      {/if}
      Refresh
    </button>
  </div>

  <form class="mt-6 flex flex-col gap-3 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/35 p-4 sm:flex-row sm:items-end" onsubmit={submitFilters}>
    <div class="min-w-0 flex-1">
      <label for="platform-filter" class="mb-1.5 block text-xs font-semibold text-base-content/60">
        Platform
      </label>
      <div class="relative">
        <Search class="pointer-events-none absolute left-3 top-1/2 z-10 h-4 w-4 -translate-y-1/2 text-base-content/80" />
        <input class="input w-full pl-9 text-sm" id="platform-filter" bind:value={platformFilter} placeholder="youtube, twitch, soundcloud..." />
      </div>
    </div>
    <div class="flex gap-2">
      <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={loading}>
        Apply
      </button>
      {#if submittedPlatform}
        <button class="btn btn-sm btn-neutral text-xs" type="button" disabled={loading} onclick={clearFilters}>
          Clear
        </button>
      {/if}
    </div>
  </form>

  <div class="mt-5 flex flex-wrap items-center justify-between gap-3">
    <p class="text-sm text-base-content/50">
      {loading ? 'Loading creators...' : `${accounts.length} ${accounts.length === 1 ? 'creator' : 'creators'} on this page`}
      {#if submittedPlatform}
        <span> · filtered to {submittedPlatform}</span>
      {/if}
    </p>
    {#if platformSummary.length > 0}
      <p class="text-xs text-base-content/40">{platformSummary.join(' · ')}</p>
    {/if}
  </div>

  {#if loadError}
    <div
      class="alert alert-error mt-6 text-sm"
      role="alert"
    >
      <CircleAlert class="h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {:else if loading}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if accounts.length === 0}
    <div class="mt-8 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/40 p-10 text-center">
      <Users class="mx-auto h-10 w-10 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No creators found</p>
      <p class="mt-1 text-sm text-base-content/50">Archived media accounts will appear here once metadata is indexed.</p>
    </div>
  {:else}
    <div class="mt-5 grid gap-5 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
      {#each accounts as account (account.accountId)}
        <article class="group rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/40 p-4 transition hover:border-base-content/20 hover:bg-base-200/65">
          <div class="flex gap-4">
            <a
              href={`/channel/${account.accountId}`}
              aria-label={`Open ${account.accountName}'s channel`}
              class={`relative grid h-16 w-16 shrink-0 place-items-center overflow-hidden rounded-box bg-gradient-to-br ${accentFor(account.accountName)} text-lg font-bold text-base-content shadow-lg shadow-black/20`}
            >
              {initialsFor(account.accountName)}
              {#if account.avatarStoragePath}
                <img
                  src={`/api/media/watch/accounts/${account.accountId}/avatar`}
                  alt=""
                  loading="lazy"
                  decoding="async"
                  class="absolute inset-0 h-full w-full object-cover"
                  onerror={hideBrokenImage}
                />
              {/if}
            </a>

            <div class="min-w-0 flex-1">
              <div class="flex min-w-0 items-start justify-between gap-2">
                <div class="min-w-0">
                  <h2 class="truncate text-base font-semibold text-base-content">
                    <a href={`/channel/${account.accountId}`} class="hover:text-primary">
                      {account.accountName}
                    </a>
                    {#if account.isVerified}
                      <span class="ml-1 text-primary" title="Verified">✓</span>
                    {/if}
                  </h2>
                  <p class="mt-0.5 truncate text-xs text-base-content/50">@{account.accountHandle}</p>
                </div>
                <span class="rounded-field bg-base-300/80 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-base-content/60">
                  {account.platform}
                </span>
              </div>

              <p class="mt-2 text-xs text-base-content/50">{accountMeta(account)}</p>

              {#if account.userNote}
                <p class="mt-2 line-clamp-2 text-xs leading-5 text-base-content/60">{account.userNote}</p>
              {/if}
            </div>
          </div>

          <div class="mt-4 flex flex-wrap items-center gap-2 border-t border-base-300/70 pt-3">
            <a
              href={`/channel/${account.accountId}`}
              class="btn btn-sm btn-neutral text-xs"
            >
              View channel
              <ChevronRight class="h-3.5 w-3.5" />
            </a>
            {#if account.accountUrl}
              <a
                href={account.accountUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="btn btn-sm btn-neutral text-xs"
              >
                Source
                <ExternalLink class="h-3.5 w-3.5" />
              </a>
            {/if}
          </div>
        </article>
      {/each}
    </div>

    <div class="mt-8 flex items-center justify-between border-t border-base-300/70 pt-5">
      <p class="text-xs text-base-content/40">Page {pageNumber}</p>
      <div class="flex gap-2">
        <button class="btn btn-sm btn-neutral text-xs" disabled={pageNumber <= 1 || loading} onclick={previousPage}>
          Previous
        </button>
        <button class="btn btn-sm btn-neutral text-xs" disabled={!hasMore || !nextCursor || loading} onclick={nextPage}>
          Next
        </button>
      </div>
    </div>
  {/if}
</section>
