<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Select, Spinner } from 'flowbite-svelte';
  import {
    ChartLineUpOutline,
    ChevronLeftOutline,
    ChevronRightOutline,
    DatabaseOutline,
    ExclamationCircleOutline,
    RectangleListOutline
  } from 'flowbite-svelte-icons';
  import { formatBytes, formatDuration, formatRelativeDate } from '$lib/media';
  import {
    getDownloadStatistics,
    getGlobalStatistics,
    listChannelStatistics,
    type ChannelStatisticsSummary,
    type DownloadHistoryBucket,
    type ListChannelStatisticsOptions,
    type StatisticsBucket,
    type StatisticsOverview
  } from '$lib/api/statistics';

  const channelPageSize = 12;
  const sortOptions = [
    { value: 'downloaded:desc', name: 'Most downloaded' },
    { value: 'available:desc', name: 'Most available' },
    { value: 'bytes:desc', name: 'Largest on disk' },
    { value: 'duration:desc', name: 'Most duration' },
    { value: 'name:asc', name: 'Name A-Z' }
  ];
  const bucketOptions = [
    { value: 'day', name: 'Last 30 days' },
    { value: 'week', name: 'Last 12 weeks' },
    { value: 'month', name: 'Last 12 months' }
  ];

  let overview = $state<StatisticsOverview | null>(null);
  let channels = $state<ChannelStatisticsSummary[]>([]);
  let downloads = $state<DownloadHistoryBucket[]>([]);
  let channelPage = $state(1);
  let totalChannels = $state(0);
  let hasMoreChannels = $state(false);
  let channelSort = $state('downloaded:desc');
  let bucket = $state<StatisticsBucket>('day');
  let loading = $state(true);
  let channelsLoading = $state(false);
  let downloadsLoading = $state(false);
  let error = $state<string | null>(null);
  let channelsError = $state<string | null>(null);
  let downloadsError = $state<string | null>(null);

  const totalChannelPages = $derived(Math.max(1, Math.ceil(totalChannels / channelPageSize)));
  const downloadTotals = $derived(
    downloads.reduce(
      (acc, item) => ({
        created: acc.created + item.created,
        completed: acc.completed + item.completed,
        failed: acc.failed + item.failed,
        bytesCompleted: acc.bytesCompleted + item.bytesCompleted
      }),
      { created: 0, completed: 0, failed: 0, bytesCompleted: 0 }
    )
  );

  onMount(() => {
    void loadAll();
  });

  async function loadAll() {
    loading = true;
    error = null;
    try {
      await Promise.all([loadOverview(), loadChannels(1), loadDownloads()]);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load statistics.';
    } finally {
      loading = false;
    }
  }

  async function loadOverview() {
    overview = await getGlobalStatistics();
  }

  async function loadChannels(targetPage: number) {
    channelsLoading = true;
    channelsError = null;
    const [sortBy, sortOrder] = channelSort.split(':') as [
      NonNullable<ListChannelStatisticsOptions['sortBy']>,
      NonNullable<ListChannelStatisticsOptions['sortOrder']>
    ];
    try {
      const response = await listChannelStatistics({
        page: targetPage,
        pageSize: channelPageSize,
        sortBy,
        sortOrder
      });
      channels = response.items;
      channelPage = response.page;
      totalChannels = response.totalCount;
      hasMoreChannels = response.hasMore;
    } catch (err) {
      channelsError = err instanceof Error ? err.message : 'Could not load channel statistics.';
    } finally {
      channelsLoading = false;
    }
  }

  async function loadDownloads() {
    downloadsLoading = true;
    downloadsError = null;
    const to = new Date();
    const from = new Date(to);
    if (bucket === 'month') {
      from.setMonth(from.getMonth() - 12);
    } else if (bucket === 'week') {
      from.setDate(from.getDate() - 84);
    } else {
      from.setDate(from.getDate() - 30);
    }

    try {
      downloads = await getDownloadStatistics({ from, to, bucket });
    } catch (err) {
      downloadsError = err instanceof Error ? err.message : 'Could not load download statistics.';
    } finally {
      downloadsLoading = false;
    }
  }

  function changeChannelSort() {
    void loadChannels(1);
  }

  function changeBucket() {
    void loadDownloads();
  }

  function channelName(channel: ChannelStatisticsSummary): string {
    return channel.accountName ?? channel.accountHandle ?? channel.sourceUrl;
  }

  function percent(value: number): string {
    return `${Math.round(value)}%`;
  }

  function compactNumber(value: number): string {
    return value.toLocaleString();
  }

  function dateRange(item: DownloadHistoryBucket): string {
    const start = new Date(item.bucketStart);
    const end = new Date(item.bucketEnd);
    if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) {
      return 'Unknown bucket';
    }
    return `${start.toLocaleDateString()} - ${end.toLocaleDateString()}`;
  }
</script>

<section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6" aria-labelledby="statistics-title">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="statistics-title" class="text-base font-bold text-slate-100">Statistics</h2>
      <p class="mt-2 text-sm text-slate-400">Global inventory, channel coverage, and download history.</p>
    </div>
    <Button
      color="dark"
      disabled={loading || channelsLoading || downloadsLoading}
      onclick={loadAll}
      class="border-slate-700! bg-slate-900/80! px-3! py-2! text-xs! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
    >
      Refresh
    </Button>
  </div>

  {#if error}
    <div class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error}</span>
    </div>
  {/if}

  {#if loading && !overview}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else}
    <div class="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
      <article class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-4">
        <p class="text-xs font-semibold uppercase tracking-wide text-slate-500">Media</p>
        <p class="mt-3 text-3xl font-bold text-white">{overview?.inventory.totalMedia.toLocaleString() ?? '-'}</p>
        <p class="mt-1 text-xs text-slate-500">{overview?.inventory.totalChannels.toLocaleString() ?? '-'} channels</p>
      </article>
      <article class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-4">
        <p class="text-xs font-semibold uppercase tracking-wide text-slate-500">Downloads</p>
        <p class="mt-3 text-3xl font-bold text-white">{overview?.inventory.totalDownloads.toLocaleString() ?? '-'}</p>
        <p class="mt-1 text-xs text-slate-500">{overview ? formatBytes(overview.inventory.totalBytes) : '-'} on disk</p>
      </article>
      <article class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-4">
        <p class="text-xs font-semibold uppercase tracking-wide text-slate-500">Duration</p>
        <p class="mt-3 text-3xl font-bold text-white">{formatDuration(overview?.inventory.totalDurationSeconds) ?? '-'}</p>
        <p class="mt-1 text-xs text-slate-500">total playable runtime</p>
      </article>
      <article class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-4">
        <p class="text-xs font-semibold uppercase tracking-wide text-slate-500">Watched</p>
        <p class="mt-3 text-3xl font-bold text-white">{overview ? percent(overview.watchProgress.watchedPercent) : '-'}</p>
        <p class="mt-1 text-xs text-slate-500">{overview?.watchProgress.watchedCount.toLocaleString() ?? '-'} completed</p>
      </article>
    </div>

    <div class="mt-5 grid gap-5 xl:grid-cols-2">
      <section class="rounded-xl border border-slate-800/80 bg-slate-950/25 p-4" aria-labelledby="media-types-title">
        <h3 id="media-types-title" class="text-sm font-bold text-slate-200">Media types</h3>
        <div class="mt-4 space-y-3">
          {#each overview?.mediaTypes ?? [] as item (item.type)}
            {@const maxCount = Math.max(...(overview?.mediaTypes ?? []).map((type) => type.count), 1)}
            <div>
              <div class="flex items-center justify-between gap-3 text-xs">
                <span class="font-semibold text-slate-300">{item.type || 'Unknown'}</span>
                <span class="text-slate-500">{compactNumber(item.count)} · {formatBytes(item.bytes)}</span>
              </div>
              <div class="mt-1 h-2 overflow-hidden rounded-full bg-slate-800">
                <div class="h-full rounded-full bg-blue-500" style={`width: ${Math.max(3, (item.count / maxCount) * 100)}%`}></div>
              </div>
            </div>
          {:else}
            <p class="text-sm text-slate-500">No media type statistics yet.</p>
          {/each}
        </div>
      </section>

      <section class="rounded-xl border border-slate-800/80 bg-slate-950/25 p-4" aria-labelledby="download-states-title">
        <h3 id="download-states-title" class="text-sm font-bold text-slate-200">Download states</h3>
        <div class="mt-4 grid gap-2 sm:grid-cols-2">
          {#each overview?.downloadStates ?? [] as state (state.state)}
            <div class="rounded-lg border border-slate-800/70 bg-slate-900/45 p-3">
              <p class="text-xs font-semibold uppercase tracking-wide text-slate-500">{state.state}</p>
              <p class="mt-2 text-xl font-bold text-white">{state.count.toLocaleString()}</p>
            </div>
          {:else}
            <p class="text-sm text-slate-500">No download state statistics yet.</p>
          {/each}
        </div>
      </section>
    </div>

    <section class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/25 p-4" aria-labelledby="download-history-title">
      <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h3 id="download-history-title" class="text-sm font-bold text-slate-200">Download history</h3>
          <p class="mt-1 text-xs text-slate-500">
            {downloadTotals.created.toLocaleString()} created · {downloadTotals.completed.toLocaleString()} completed · {downloadTotals.failed.toLocaleString()} failed · {formatBytes(downloadTotals.bytesCompleted)} completed
          </p>
        </div>
        <Select
          items={bucketOptions}
          bind:value={bucket}
          onchange={changeBucket}
          aria-label="Download history range"
          class="w-44! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500!"
        />
      </div>

      {#if downloadsError}
        <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{downloadsError}</span>
        </div>
      {:else if downloadsLoading}
        <div class="mt-8 flex justify-center"><Spinner size="6" /></div>
      {:else if downloads.length === 0}
        <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
          <ChartLineUpOutline class="mx-auto h-9 w-9 text-slate-700" />
          <p class="mt-4 text-sm font-semibold text-slate-300">No download history</p>
        </div>
      {:else}
        <div class="mt-4 space-y-2">
          {#each downloads.slice(-10).reverse() as item (item.bucketStart)}
            {@const maxCompleted = Math.max(...downloads.map((download) => download.completed), 1)}
            <article class="rounded-lg border border-slate-800/70 bg-slate-900/45 p-3">
              <div class="flex flex-wrap items-center justify-between gap-2 text-xs">
                <span class="font-semibold text-slate-300">{dateRange(item)}</span>
                <span class="text-slate-500">{item.completed.toLocaleString()} completed · {formatBytes(item.bytesCompleted)}</span>
              </div>
              <div class="mt-2 h-2 overflow-hidden rounded-full bg-slate-800">
                <div class="h-full rounded-full bg-emerald-500" style={`width: ${Math.max(3, (item.completed / maxCompleted) * 100)}%`}></div>
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </section>

    <section class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/25 p-4" aria-labelledby="channel-stats-title">
      <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h3 id="channel-stats-title" class="text-sm font-bold text-slate-200">Channel statistics</h3>
          <p class="mt-1 text-xs text-slate-500">{totalChannels.toLocaleString()} tracked creator sources</p>
        </div>
        <Select
          items={sortOptions}
          bind:value={channelSort}
          onchange={changeChannelSort}
          aria-label="Sort channel statistics"
          class="w-48! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500!"
        />
      </div>

      {#if channelsError}
        <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{channelsError}</span>
        </div>
      {:else if channelsLoading}
        <div class="mt-8 flex justify-center"><Spinner size="6" /></div>
      {:else if channels.length === 0}
        <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
          <RectangleListOutline class="mx-auto h-9 w-9 text-slate-700" />
          <p class="mt-4 text-sm font-semibold text-slate-300">No channel statistics</p>
        </div>
      {:else}
        <div class="mt-4 overflow-x-auto">
          <table class="min-w-full divide-y divide-slate-800 text-left text-sm">
            <thead class="text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th class="py-3 pr-4 font-semibold">Channel</th>
                <th class="px-4 py-3 font-semibold">Coverage</th>
                <th class="px-4 py-3 font-semibold">Duration</th>
                <th class="px-4 py-3 font-semibold">Bytes</th>
                <th class="py-3 pl-4 font-semibold">Last scan</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800/70">
              {#each channels as channel (channel.creatorSourceId)}
                <tr class="text-slate-300">
                  <td class="py-3 pr-4">
                    <div class="flex min-w-56 items-center gap-3">
                      <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                        <DatabaseOutline class="h-4.5 w-4.5" />
                      </span>
                      <div class="min-w-0">
                        {#if channel.accountId}
                          <a href={`/channel/${channel.accountId}`} class="truncate font-semibold text-slate-100 hover:text-blue-300">{channelName(channel)}</a>
                        {:else}
                          <p class="truncate font-semibold text-slate-100">{channelName(channel)}</p>
                        {/if}
                        <p class="mt-0.5 truncate text-xs text-slate-500">{channel.platform} · {channel.sourceType}</p>
                      </div>
                    </div>
                  </td>
                  <td class="px-4 py-3">
                    <p class="font-semibold text-slate-100">{percent(channel.downloadedPercent)}</p>
                    <p class="mt-0.5 text-xs text-slate-500">{channel.downloadedCount.toLocaleString()} / {channel.availableCount.toLocaleString()}</p>
                  </td>
                  <td class="px-4 py-3 text-slate-400">{formatDuration(channel.downloadedDurationSeconds) ?? '-'}</td>
                  <td class="px-4 py-3 text-slate-400">{formatBytes(channel.totalBytes)}</td>
                  <td class="py-3 pl-4 text-slate-500">{formatRelativeDate(channel.lastSuccessfulScanAt) ?? 'Never'}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>

        <div class="mt-4 flex items-center justify-between border-t border-slate-800/70 pt-4">
          <p class="text-xs text-slate-600">Page {channelPage} of {totalChannelPages}</p>
          <div class="flex gap-2">
            <Button
              color="dark"
              disabled={channelPage <= 1 || channelsLoading}
              onclick={() => loadChannels(channelPage - 1)}
              class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
            >
              <ChevronLeftOutline class="mr-1 h-3.5 w-3.5" />
              Previous
            </Button>
            <Button
              color="dark"
              disabled={!hasMoreChannels || channelsLoading}
              onclick={() => loadChannels(channelPage + 1)}
              class="border-slate-700! bg-slate-900! px-3! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800! disabled:opacity-40"
            >
              Next
              <ChevronRightOutline class="ml-1 h-3.5 w-3.5" />
            </Button>
          </div>
        </div>
      {/if}
    </section>
  {/if}
</section>
