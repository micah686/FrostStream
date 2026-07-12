<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Spinner } from 'flowbite-svelte';
  import {
    ArrowUpRightFromSquareOutline,
    ChevronRightOutline,
    ExclamationCircleOutline,
    RefreshOutline,
    SearchOutline,
    UsersGroupOutline
  } from 'flowbite-svelte-icons';
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
      <h1 id="library-creators-title" class="text-2xl font-bold tracking-tight text-white">Creators</h1>
      <p class="mt-1 text-sm text-slate-500">
        Creator accounts discovered from archived metadata.
      </p>
    </div>
    <Button
      color="dark"
      disabled={loading}
      onclick={() => loadPage(null, 1, [])}
      class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
    >
      {#if loading}
        <Spinner size="4" class="mr-1.5" />
      {:else}
        <RefreshOutline class="mr-1.5 h-4 w-4" />
      {/if}
      Refresh
    </Button>
  </div>

  <form class="mt-6 flex flex-col gap-3 rounded-2xl border border-slate-800/80 bg-slate-900/35 p-4 sm:flex-row sm:items-end" onsubmit={submitFilters}>
    <div class="min-w-0 flex-1">
      <label for="platform-filter" class="mb-1.5 block text-xs font-semibold text-slate-400">
        Platform
      </label>
      <div class="relative">
        <SearchOutline class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-600" />
        <Input
          id="platform-filter"
          bind:value={platformFilter}
          placeholder="youtube, twitch, soundcloud..."
          class="border-slate-700! bg-slate-950/60! pl-9! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
        />
      </div>
    </div>
    <div class="flex gap-2">
      <Button
        type="submit"
        color="blue"
        disabled={loading}
        class="border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-50"
      >
        Apply
      </Button>
      {#if submittedPlatform}
        <Button
          type="button"
          color="dark"
          disabled={loading}
          onclick={clearFilters}
          class="border-slate-700! bg-slate-900! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
        >
          Clear
        </Button>
      {/if}
    </div>
  </form>

  <div class="mt-5 flex flex-wrap items-center justify-between gap-3">
    <p class="text-sm text-slate-500">
      {loading ? 'Loading creators...' : `${accounts.length} ${accounts.length === 1 ? 'creator' : 'creators'} on this page`}
      {#if submittedPlatform}
        <span> · filtered to {submittedPlatform}</span>
      {/if}
    </p>
    {#if platformSummary.length > 0}
      <p class="text-xs text-slate-600">{platformSummary.join(' · ')}</p>
    {/if}
  </div>

  {#if loadError}
    <div
      class="mt-6 flex items-center gap-3 rounded-xl border border-red-900/60 bg-red-950/40 p-4 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {:else if loading}
    <div class="mt-16 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if accounts.length === 0}
    <div class="mt-8 rounded-2xl border border-slate-800/80 bg-slate-900/40 p-10 text-center">
      <UsersGroupOutline class="mx-auto h-10 w-10 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No creators found</p>
      <p class="mt-1 text-sm text-slate-500">Archived media accounts will appear here once metadata is indexed.</p>
    </div>
  {:else}
    <div class="mt-5 grid gap-5 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
      {#each accounts as account (account.accountId)}
        <article class="group rounded-2xl border border-slate-800/80 bg-slate-900/40 p-4 transition hover:border-slate-700 hover:bg-slate-900/65">
          <div class="flex gap-4">
            <a
              href={`/channel/${account.accountId}`}
              aria-label={`Open ${account.accountName}'s channel`}
              class={`relative grid h-16 w-16 shrink-0 place-items-center overflow-hidden rounded-2xl bg-gradient-to-br ${accentFor(account.accountName)} text-lg font-bold text-white shadow-lg shadow-black/20`}
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
                  <h2 class="truncate text-base font-semibold text-slate-100">
                    <a href={`/channel/${account.accountId}`} class="hover:text-blue-300">
                      {account.accountName}
                    </a>
                    {#if account.isVerified}
                      <span class="ml-1 text-blue-400" title="Verified">✓</span>
                    {/if}
                  </h2>
                  <p class="mt-0.5 truncate text-xs text-slate-500">@{account.accountHandle}</p>
                </div>
                <span class="rounded-md bg-slate-800/80 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-slate-400">
                  {account.platform}
                </span>
              </div>

              <p class="mt-2 text-xs text-slate-500">{accountMeta(account)}</p>

              {#if account.userNote}
                <p class="mt-2 line-clamp-2 text-xs leading-5 text-slate-400">{account.userNote}</p>
              {/if}
            </div>
          </div>

          <div class="mt-4 flex flex-wrap items-center gap-2 border-t border-slate-800/70 pt-3">
            <a
              href={`/channel/${account.accountId}`}
              class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
            >
              View channel
              <ChevronRightOutline class="h-3.5 w-3.5" />
            </a>
            {#if account.accountUrl}
              <a
                href={account.accountUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-300 transition hover:border-slate-600 hover:bg-slate-800"
              >
                Source
                <ArrowUpRightFromSquareOutline class="h-3.5 w-3.5" />
              </a>
            {/if}
          </div>
        </article>
      {/each}
    </div>

    <div class="mt-8 flex items-center justify-between border-t border-slate-800/70 pt-5">
      <p class="text-xs text-slate-600">Page {pageNumber}</p>
      <div class="flex gap-2">
        <Button
          color="dark"
          disabled={pageNumber <= 1 || loading}
          onclick={previousPage}
          class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
        >
          Previous
        </Button>
        <Button
          color="dark"
          disabled={!hasMore || !nextCursor || loading}
          onclick={nextPage}
          class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
        >
          Next
        </Button>
      </div>
    </div>
  {/if}
</section>
