<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import type { ChartConfiguration } from 'chart.js';
  import {
    ChartNoAxesCombined,
    CircleAlert,
    Clock3,
    Database,
    Download,
    HardDrive,
    RefreshCw,
    Search,
    Tv,
    X
  } from '@lucide/svelte';
  import { Select } from '$lib/components/ui';
  import StatisticsChart from '$lib/components/admin/StatisticsChart.svelte';
  import { categoricalPalette, readChartTheme, stateColors, withAlpha, type ChartTheme } from '$lib/charts/theme';
  import { theme } from '$lib/stores/theme';
  import { formatBytes } from '$lib/media';
  import {
    getChannelStatistics,
    getChannelStatisticsByAccount,
    getDownloadStatistics,
    getGlobalStatistics,
    listChannelStatistics,
    suggestChannelStatistics,
    type ChannelStatisticsDetail,
    type ChannelSuggestion,
    type ChannelStatisticsSummary,
    type DownloadHistoryBucket,
    type StatisticsBucket,
    type StatisticsOverview
  } from '$lib/api/statistics';

  type RangePreset = '30d' | '60d' | '90d' | '12m' | 'custom';
  type Interval = StatisticsBucket | 'year';
  const rangeOptions = [
    { value: '30d', name: 'Last 30 days' },
    { value: '60d', name: 'Last 60 days' },
    { value: '90d', name: 'Last 90 days' },
    { value: '12m', name: 'Last 12 months' },
    { value: 'custom', name: 'Custom range' }
  ];
  const intervalOptions = [
    { value: 'day', name: 'Daily' },
    { value: 'week', name: 'Weekly' },
    { value: 'month', name: 'Monthly' },
    { value: 'year', name: 'Yearly' }
  ];

  let overview = $state<StatisticsOverview | null>(null);
  let allActivity = $state<DownloadHistoryBucket[]>([]);
  let customActivity = $state<DownloadHistoryBucket[]>([]);
  let mostDownloaded = $state<ChannelStatisticsSummary[]>([]);
  let longestDuration = $state<ChannelStatisticsSummary[]>([]);
  let largestOnDisk = $state<ChannelStatisticsSummary[]>([]);
  let allChannels = $state<ChannelStatisticsSummary[]>([]);
  let coverageDetails = $state<ChannelStatisticsDetail[]>([]);
  let coverageLoading = $state(false);
  let coverageError = $state<string | null>(null);
  let selectedChannel = $state<ChannelStatisticsDetail | null>(null);
  let channelResults = $state<ChannelSuggestion[]>([]);
  let channelQuery = $state('');
  let channelSearchDraft = $state('');
  let channelSearchOpen = $state(false);
  let channelSearchLoading = $state(false);
  let channelLoading = $state(false);
  let rangePreset = $state<RangePreset>('30d');
  let interval = $state<Interval>('day');
  let dateFrom = $state('');
  let dateTo = $state('');
  let loading = $state(true);
  let activityLoading = $state(false);
  let error = $state<string | null>(null);
  let activityError = $state<string | null>(null);
  let channelError = $state<string | null>(null);
  let channelSearchTimer: ReturnType<typeof setTimeout> | null = null;
  let channelSearchRequest = 0;
  let chartTheme = $state<ChartTheme>(readChartTheme());
  let themeFrame: number | null = null;

  const mediaMixChart = $derived.by((): ChartConfiguration => ({
    type: 'doughnut',
    data: {
      labels: (overview?.mediaTypes ?? []).map((item) => displayLabel(item.type)),
      datasets: [{
        data: (overview?.mediaTypes ?? []).map((item) => item.count),
        backgroundColor: categoricalPalette(chartTheme, overview?.mediaTypes.length ?? 0),
        borderColor: chartTheme.base100,
        borderWidth: 3,
        hoverOffset: 5
      }]
    },
    options: doughnutOptions()
  }));

  const downloadHealthChart = $derived.by((): ChartConfiguration => ({
    type: 'doughnut',
    data: {
      labels: (overview?.downloadStates ?? []).map((item) => displayLabel(item.state)),
      datasets: [{
        data: (overview?.downloadStates ?? []).map((item) => item.count),
        backgroundColor: stateColors((overview?.downloadStates ?? []).map((item) => item.state), chartTheme),
        borderColor: chartTheme.base100,
        borderWidth: 3,
        hoverOffset: 5
      }]
    },
    options: doughnutOptions()
  }));

  const allActivityChart = $derived.by(() => activityChart(allActivity, 'day'));
  const customActivityChart = $derived.by(() => activityChart(customActivity, interval));
  const mostDownloadedChart = $derived.by(() => polarChart(
    mostDownloaded,
    (item) => item.downloadedCount,
    (value) => `${value.toLocaleString()} ${value === 1 ? 'download' : 'downloads'}`
  ));
  const longestDurationChart = $derived.by(() => polarChart(
    longestDuration,
    (item) => item.downloadedDurationSeconds,
    formatLongDuration
  ));
  const largestOnDiskChart = $derived.by(() => polarChart(
    largestOnDisk,
    (item) => item.totalBytes,
    formatBytes
  ));
  const platformTypesChart = $derived.by(() => polarValueChart(
    platformTotals().map((item) => displayLabel(item.label)),
    platformTotals().map((item) => item.value),
    (value) => `${value.toLocaleString()} media`
  ));
  const coverageChart = $derived.by(() => polarValueChart(
    ['Available', 'Unavailable', 'Ignored', 'Removed'],
    coverageTotals(),
    (value) => `${value.toLocaleString()} media`,
    [chartTheme.success, chartTheme.warning, chartTheme.neutral, chartTheme.error]
  ));

  const channelAvailabilityChart = $derived.by((): ChartConfiguration => {
    const detail = selectedChannel;
    return pieChart(
      ['Available', 'Unavailable', 'Ignored', 'Removed'],
      detail
        ? [detail.summary.availableCount, detail.unavailableCount, detail.ignoredCount, detail.removedCount]
        : [],
      [chartTheme.success, chartTheme.warning, chartTheme.neutral, chartTheme.error]
    );
  });

  const channelDownloadStatesChart = $derived.by((): ChartConfiguration => pieChart(
    (selectedChannel?.recentDownloadStates ?? []).map((item) => displayLabel(item.state)),
    (selectedChannel?.recentDownloadStates ?? []).map((item) => item.count),
      stateColors((selectedChannel?.recentDownloadStates ?? []).map((item) => item.state), chartTheme)
  ));

  const channelMediaTypesChart = $derived.by((): ChartConfiguration => pieChart(
    (selectedChannel?.mediaTypes ?? []).map((item) => displayLabel(item.type)),
    (selectedChannel?.mediaTypes ?? []).map((item) => item.count),
    categoricalPalette(chartTheme, selectedChannel?.mediaTypes.length ?? 0)
  ));

  onMount(() => {
    const unsubscribe = theme.subscribe(() => {
      if (themeFrame !== null) cancelAnimationFrame(themeFrame);
      themeFrame = requestAnimationFrame(() => {
        chartTheme = readChartTheme();
        themeFrame = null;
      });
    });
    setPresetDates('30d');
    void loadAll();
    return () => {
      unsubscribe();
      if (themeFrame !== null) cancelAnimationFrame(themeFrame);
    };
  });

  onDestroy(() => {
    if (channelSearchTimer) clearTimeout(channelSearchTimer);
  });

  async function loadAll() {
    loading = true;
    error = null;
    try {
      const now = new Date();
      const retainedFrom = new Date(now);
      retainedFrom.setDate(retainedFrom.getDate() - 730);
      const [overviewResult, activityResult, downloadedResult, durationResult, bytesResult, allChannelsResult] = await Promise.allSettled([
        getGlobalStatistics(),
        getDownloadStatistics({ from: retainedFrom, to: now, bucket: 'day' }),
        listChannelStatistics({ page: 1, pageSize: 10, sortBy: 'downloaded', sortOrder: 'desc' }),
        listChannelStatistics({ page: 1, pageSize: 10, sortBy: 'duration', sortOrder: 'desc' }),
        listChannelStatistics({ page: 1, pageSize: 10, sortBy: 'bytes', sortOrder: 'desc' }),
        loadAllChannelSummaries()
      ]);
      if (overviewResult.status === 'rejected') throw overviewResult.reason;
      overview = overviewResult.value;
      if (activityResult.status === 'fulfilled') allActivity = activityResult.value;
      if (downloadedResult.status === 'fulfilled') mostDownloaded = downloadedResult.value.items;
      if (durationResult.status === 'fulfilled') longestDuration = durationResult.value.items;
      if (bytesResult.status === 'fulfilled') largestOnDisk = bytesResult.value.items;
      if (allChannelsResult.status === 'fulfilled') {
        allChannels = allChannelsResult.value;
        await loadCoverageDetails(allChannels);
      }
      await loadCustomActivity();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load StatsNew.';
    } finally {
      loading = false;
    }
  }

  async function loadAllChannelSummaries(): Promise<ChannelStatisticsSummary[]> {
    const items: ChannelStatisticsSummary[] = [];
    let page = 1;
    let hasMore = true;
    while (hasMore) {
      const response = await listChannelStatistics({
        page,
        pageSize: 100,
        sortBy: 'available',
        sortOrder: 'desc'
      });
      items.push(...response.items);
      hasMore = response.hasMore;
      page += 1;
    }
    return items;
  }

  async function loadCoverageDetails(channels: ChannelStatisticsSummary[]) {
    coverageLoading = true;
    coverageError = null;
    try {
      const details: ChannelStatisticsDetail[] = [];
      for (let index = 0; index < channels.length; index += 8) {
        const batch = channels.slice(index, index + 8);
        const results = await Promise.allSettled(batch.map(async (channel) => {
          if (channel.creatorSourceId !== null) return getChannelStatistics(channel.creatorSourceId);
          if (channel.accountId !== null) return getChannelStatisticsByAccount(channel.accountId);
          return null;
        }));
        details.push(...results
          .filter((result): result is PromiseFulfilledResult<ChannelStatisticsDetail | null> => result.status === 'fulfilled')
          .map((result) => result.value)
          .filter((detail): detail is ChannelStatisticsDetail => detail !== null));
      }
      if (details.length === 0 && channels.length > 0) {
        throw new Error('Could not load coverage statistics for any channel.');
      }
      coverageDetails = details;
    } catch (err) {
      coverageDetails = [];
      coverageError = err instanceof Error ? err.message : 'Could not load coverage statistics.';
    } finally {
      coverageLoading = false;
    }
  }

  async function loadCustomActivity() {
    const range = selectedRange();
    if (!range) return;
    activityLoading = true;
    activityError = null;
    try {
      const requestedBucket: StatisticsBucket = interval === 'year' ? 'month' : interval;
      const buckets = await getDownloadStatistics({ from: range.from, to: range.to, bucket: requestedBucket });
      customActivity = interval === 'year' ? aggregateYears(buckets) : buckets;
    } catch (err) {
      activityError = err instanceof Error ? err.message : 'Could not load custom download activity.';
    } finally {
      activityLoading = false;
    }
  }

  function changeRange() {
    if (rangePreset !== 'custom') setPresetDates(rangePreset);
    void loadCustomActivity();
  }

  function setPresetDates(preset: Exclude<RangePreset, 'custom'>) {
    const to = new Date();
    const from = new Date(to);
    if (preset === '12m') from.setMonth(from.getMonth() - 12);
    else from.setDate(from.getDate() - Number.parseInt(preset, 10));
    dateFrom = dateInput(from);
    dateTo = dateInput(to);
  }

  function selectedRange(): { from: Date; to: Date } | null {
    const from = new Date(`${dateFrom}T00:00:00`);
    const to = new Date(`${dateTo}T23:59:59.999`);
    if (!dateFrom || !dateTo || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
      activityError = 'Choose both a start and end date.';
      return null;
    }
    if (from >= to) {
      activityError = 'The start date must be earlier than the end date.';
      return null;
    }
    return { from, to };
  }

  function aggregateYears(items: DownloadHistoryBucket[]): DownloadHistoryBucket[] {
    const years = new Map<number, DownloadHistoryBucket>();
    for (const item of items) {
      const year = new Date(item.bucketStart).getFullYear();
      const current = years.get(year) ?? {
        bucketStart: new Date(year, 0, 1).toISOString(),
        bucketEnd: new Date(year + 1, 0, 1).toISOString(),
        created: 0,
        completed: 0,
        failed: 0,
        cancelled: 0,
        ignored: 0,
        bytesCompleted: 0,
        durationCompletedSeconds: 0
      };
      current.created += item.created;
      current.completed += item.completed;
      current.failed += item.failed;
      current.cancelled += item.cancelled;
      current.ignored += item.ignored;
      current.bytesCompleted += item.bytesCompleted;
      current.durationCompletedSeconds += item.durationCompletedSeconds;
      years.set(year, current);
    }
    return [...years.values()];
  }

  function scheduleChannelSearch() {
    if (channelSearchTimer) clearTimeout(channelSearchTimer);
    const query = channelQuery.trim();
    if (query.length < 2) {
      channelResults = [];
      channelSearchOpen = false;
      channelSearchLoading = false;
      return;
    }
    channelSearchOpen = true;
    channelSearchTimer = setTimeout(() => void searchChannels(query), 180);
  }

  async function searchChannels(query: string) {
    const request = ++channelSearchRequest;
    channelSearchLoading = true;
    try {
      const response = await suggestChannelStatistics(query, 8);
      if (request !== channelSearchRequest) return;
      channelResults = response;
      channelSearchOpen = true;
    } catch {
      if (request === channelSearchRequest) channelResults = [];
    } finally {
      if (request === channelSearchRequest) channelSearchLoading = false;
    }
  }

  async function chooseChannel(suggestion: ChannelSuggestion) {
    channelLoading = true;
    channelError = null;
    channelSearchOpen = false;
    channelSearchDraft = suggestion.value;
    channelQuery = suggestion.value;
    try {
      // Channels are keyed on accounts; only subscription-driven ones also have a creator source.
      if (suggestion.accountId !== null && suggestion.accountId !== undefined) {
        selectedChannel = await getChannelStatisticsByAccount(suggestion.accountId);
      } else if (suggestion.creatorSourceId !== null && suggestion.creatorSourceId !== undefined) {
        selectedChannel = await getChannelStatistics(suggestion.creatorSourceId);
      } else {
        throw new Error('That channel has no statistics to show yet.');
      }
    } catch (err) {
      channelError = err instanceof Error ? err.message : 'Could not load channel statistics.';
    } finally {
      channelLoading = false;
    }
  }

  function clearChannel() {
    channelSearchDraft = '';
    channelQuery = '';
    selectedChannel = null;
    channelResults = [];
    channelSearchOpen = false;
    channelError = null;
  }

  function handleChannelSearchKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && channelResults.length > 0) {
      event.preventDefault();
      void chooseChannel(channelResults[0]);
    } else if (event.key === 'Escape') {
      channelSearchOpen = false;
    }
  }

  function activityChart(items: DownloadHistoryBucket[], chartInterval: Interval): ChartConfiguration {
    const datasets = [
      { label: 'Completed', key: 'completed' as const, color: chartTheme.success },
      { label: 'Failed', key: 'failed' as const, color: chartTheme.error },
      { label: 'Cancelled', key: 'cancelled' as const, color: chartTheme.warning },
      { label: 'Ignored', key: 'ignored' as const, color: chartTheme.neutral },
      { label: 'Created', key: 'created' as const, color: chartTheme.info }
    ];
    return {
      type: 'line',
      data: {
        labels: items.map((item) => bucketLabel(item.bucketStart, chartInterval)),
        datasets: datasets.map((dataset) => ({
          label: dataset.label,
          data: items.map((item) => item[dataset.key]),
          borderColor: dataset.color,
          backgroundColor: withAlpha(dataset.color, 8),
          pointBackgroundColor: dataset.color,
          pointRadius: items.length > 45 ? 0 : 2,
          pointHoverRadius: 5,
          borderWidth: 2,
          tension: 0.3,
          fill: dataset.key === 'completed'
        }))
      },
      options: cartesianOptions()
    };
  }

  function polarChart(
    items: ChannelStatisticsSummary[],
    value: (item: ChannelStatisticsSummary) => number,
    format: (value: number) => string
  ): ChartConfiguration {
    return polarValueChart(items.map(channelName), items.map(value), format);
  }

  function polarValueChart(
    labels: string[],
    values: number[],
    format: (value: number) => string,
    colors = categoricalPalette(chartTheme, labels.length).map((color) => withAlpha(color, 68))
  ): ChartConfiguration {
    const plugins = legendOptions();
    return {
      type: 'polarArea',
      data: {
        labels,
        datasets: [{
          data: values,
          backgroundColor: colors,
          borderColor: chartTheme.base100,
          borderWidth: 2
        }]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        scales: {
          r: {
            beginAtZero: true,
            grid: { color: withAlpha(chartTheme.base300, 55) },
            angleLines: { color: withAlpha(chartTheme.base300, 55) },
            ticks: { display: false }
          }
        },
        plugins: {
          ...plugins,
          // The channel name is already the tooltip title, so the body is just the value.
          tooltip: {
            ...plugins.tooltip,
            callbacks: { label: (context) => ` ${format(context.parsed.r)}` }
          }
        }
      }
    };
  }

  function platformTotals(): { label: string; value: number }[] {
    const totals = new Map<string, number>();
    for (const channel of allChannels) {
      totals.set(channel.platform, (totals.get(channel.platform) ?? 0) + channel.availableCount);
    }
    return [...totals.entries()]
      .map(([label, value]) => ({ label, value }))
      .sort((left, right) => right.value - left.value)
      .slice(0, 10);
  }

  function coverageTotals(): number[] {
    let available = 0;
    let unavailable = 0;
    let ignored = 0;
    let removed = 0;
    for (const detail of coverageDetails) {
      available += detail.summary.availableCount;
      unavailable += detail.unavailableCount;
      ignored += detail.ignoredCount;
      removed += detail.removedCount;
    }
    return [available, unavailable, ignored, removed];
  }

  function pieChart(labels: string[], data: number[], colors: string[]): ChartConfiguration {
    return {
      type: 'pie',
      data: {
        labels,
        datasets: [{
          data,
          backgroundColor: colors,
          borderColor: chartTheme.base100,
          borderWidth: 3,
          hoverOffset: 5
        }]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: legendOptions()
      }
    };
  }

  function doughnutOptions(): ChartConfiguration<'doughnut'>['options'] {
    return {
      responsive: true,
      maintainAspectRatio: false,
      cutout: '68%',
      plugins: legendOptions()
    };
  }

  function cartesianOptions(): ChartConfiguration<'line'>['options'] {
    return {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: 'index', intersect: false },
      plugins: legendOptions(),
      scales: {
        x: {
          grid: { display: false },
          ticks: { color: withAlpha(chartTheme.baseContent, 55), maxTicksLimit: 12, maxRotation: 0 }
        },
        y: {
          beginAtZero: true,
          grid: { color: withAlpha(chartTheme.base300, 50) },
          ticks: { color: withAlpha(chartTheme.baseContent, 55), precision: 0 }
        }
      }
    };
  }

  function legendOptions() {
    return {
      legend: {
        position: 'right' as const,
        labels: {
          color: withAlpha(chartTheme.baseContent, 70),
          usePointStyle: true,
          pointStyle: 'circle' as const,
          boxWidth: 8,
          boxHeight: 8,
          padding: 14,
          font: { size: 11 }
        }
      },
      tooltip: {
        backgroundColor: chartTheme.base100,
        borderColor: chartTheme.base300,
        borderWidth: 1,
        titleColor: chartTheme.baseContent,
        bodyColor: chartTheme.baseContent,
        padding: 12
      }
    };
  }

  function displayLabel(value: string): string {
    return (value || 'Unknown').replaceAll('_', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());
  }

  function channelName(channel: ChannelStatisticsSummary): string {
    return channel.accountName ?? channel.accountHandle ?? channel.sourceUrl ?? 'Unknown channel';
  }

  function bucketLabel(value: string, chartInterval: Interval): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'Unknown';
    if (chartInterval === 'year') return String(date.getFullYear());
    if (chartInterval === 'month') return date.toLocaleDateString(undefined, { month: 'short', year: '2-digit' });
    return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  function dateInput(value: Date): string {
    const year = value.getFullYear();
    const month = String(value.getMonth() + 1).padStart(2, '0');
    const day = String(value.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  function formatLongDuration(value?: number | null): string {
    if (value == null || !Number.isFinite(value)) return '—';
    let minutes = Math.max(0, Math.floor(value / 60));
    const units = [
      ['year', 525_600],
      ['month', 43_800],
      ['week', 10_080],
      ['day', 1_440],
      ['hour', 60],
      ['minute', 1]
    ] as const;
    const parts: string[] = [];
    for (const [label, size] of units) {
      const count = Math.floor(minutes / size);
      if (count > 0 || (label === 'minute' && parts.length === 0)) {
        parts.push(`${count.toLocaleString()} ${label}${count === 1 ? '' : 's'}`);
        minutes %= size;
      }
    }
    return parts.join(' ');
  }
</script>

<svelte:head>
  <title>StatsNew · FrostStream</title>
</svelte:head>

<section class="card border border-base-300 bg-base-100 p-5 sm:p-6" aria-labelledby="stats-new-title">
  <div class="flex flex-wrap items-start justify-between gap-4">
    <div>
      <h1 id="stats-new-title" class="text-xl font-bold text-base-content">StatsNew</h1>
      <p class="mt-2 max-w-3xl text-sm text-base-content/60">
        A complete view of library inventory, download activity, channel leaders, and individual channel coverage.
      </p>
    </div>
    <button class="btn btn-sm btn-neutral text-xs" disabled={loading || activityLoading} onclick={loadAll}>
      <RefreshCw class={['mr-1.5 h-4 w-4', (loading || activityLoading) && 'animate-spin']} />
      Refresh
    </button>
  </div>

  {#if error}
    <div class="alert alert-error mt-5 text-sm" role="alert">
      <CircleAlert class="h-4 w-4 shrink-0" />
      <span>{error}</span>
    </div>
  {/if}

  {#if loading && !overview}
    <div class="flex min-h-72 items-center justify-center"><span class="loading loading-spinner loading-md"></span></div>
  {:else}
    <div class="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
      <article class="card border border-base-300 bg-base-100 p-4">
        <div class="flex items-start justify-between gap-3">
          <div><p class="text-xs text-base-content/50">Library media</p><p class="mt-2 text-2xl font-bold text-base-content">{overview?.inventory.totalMedia.toLocaleString() ?? '—'}</p></div>
          <ChartNoAxesCombined class="h-5 w-5 text-primary" />
        </div>
      </article>
      <article class="card border border-base-300 bg-base-100 p-4">
        <div class="flex items-start justify-between gap-3">
          <div><p class="text-xs text-base-content/50">Total channels</p><p class="mt-2 text-2xl font-bold text-base-content">{overview?.inventory.totalChannels.toLocaleString() ?? '—'}</p></div>
          <Tv class="h-5 w-5 text-info" />
        </div>
      </article>
      <article class="card border border-base-300 bg-base-100 p-4">
        <div class="flex items-start justify-between gap-3">
          <div><p class="text-xs text-base-content/50">Total downloaded</p><p class="mt-2 text-2xl font-bold text-base-content">{overview?.inventory.totalDownloads.toLocaleString() ?? '—'}</p></div>
          <Download class="h-5 w-5 text-success" />
        </div>
      </article>
      <article class="card border border-base-300 bg-base-100 p-4">
        <div class="flex items-start justify-between gap-3">
          <div><p class="text-xs text-base-content/50">Total size</p><p class="mt-2 text-2xl font-bold text-base-content">{overview ? formatBytes(overview.inventory.totalBytes) : '—'}</p></div>
          <HardDrive class="h-5 w-5 text-warning" />
        </div>
      </article>
      <article class="card border border-base-300 bg-base-100 p-4">
        <div class="flex items-start justify-between gap-3">
          <div><p class="text-xs text-base-content/50">Total duration</p><p class="mt-2 text-lg font-bold leading-snug text-base-content">{formatLongDuration(overview?.inventory.totalDurationSeconds)}</p></div>
          <Clock3 class="h-5 w-5 text-accent" />
        </div>
      </article>
    </div>

    <div class="mt-5 grid gap-5 xl:grid-cols-2">
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Media mix</h2>
        <p class="mt-1 text-xs text-base-content/50">Library media grouped by type</p>
        {#if (overview?.mediaTypes.length ?? 0) > 0}
          <div class="mt-4"><StatisticsChart config={mediaMixChart} ariaLabel="Doughnut chart showing the library media mix" height="17rem" /></div>
        {:else}
          <p class="mt-8 text-center text-sm text-base-content/50">No media type data.</p>
        {/if}
      </section>
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Download health</h2>
        <p class="mt-1 text-xs text-base-content/50">Download jobs grouped by their current state</p>
        {#if (overview?.downloadStates.length ?? 0) > 0}
          <div class="mt-4"><StatisticsChart config={downloadHealthChart} ariaLabel="Doughnut chart showing download health" height="17rem" /></div>
        {:else}
          <p class="mt-8 text-center text-sm text-base-content/50">No download state data.</p>
        {/if}
      </section>
    </div>

    <section class="card mt-5 border border-base-300 bg-base-100 p-5">
      <h2 class="text-sm font-bold text-base-content">All download activity</h2>
      <p class="mt-1 text-xs text-base-content/50">All retained daily download-state activity</p>
      {#if allActivity.length > 0}
        <div class="mt-4"><StatisticsChart config={allActivityChart} ariaLabel="Line chart showing all retained download activity" height="22rem" /></div>
      {:else}
        <p class="mt-8 text-center text-sm text-base-content/50">No retained download activity.</p>
      {/if}
    </section>

    <section class="card mt-5 border border-base-300 bg-base-100 p-5">
      <div class="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div>
          <h2 class="text-sm font-bold text-base-content">Custom download activity</h2>
          <p class="mt-1 text-xs text-base-content/50">Download states over a selected range and interval</p>
        </div>
        <div class="flex flex-wrap gap-2">
          <label>
            <span class="mb-1 block text-xs text-base-content/50">Range</span>
            <Select items={rangeOptions} bind:value={rangePreset} onchange={changeRange} class="w-40 text-sm" />
          </label>
          <label>
            <span class="mb-1 block text-xs text-base-content/50">Interval</span>
            <Select items={intervalOptions} bind:value={interval} onchange={() => void loadCustomActivity()} class="w-32 text-sm" />
          </label>
        </div>
      </div>
      {#if rangePreset === 'custom'}
        <div class="mt-4 flex flex-wrap items-end gap-3 rounded-xl border border-base-300 bg-base-100 p-3">
          <label><span class="mb-1 block text-xs text-base-content/50">From</span><input class="input input-sm" type="date" bind:value={dateFrom} max={dateTo || undefined} /></label>
          <label><span class="mb-1 block text-xs text-base-content/50">To</span><input class="input input-sm" type="date" bind:value={dateTo} min={dateFrom || undefined} /></label>
          <button class="btn btn-sm btn-primary text-xs" onclick={loadCustomActivity}>Apply range</button>
        </div>
      {/if}
      {#if activityError}
        <div class="alert alert-error mt-4 text-sm" role="alert">{activityError}</div>
      {:else if activityLoading}
        <div class="mt-6 flex min-h-64 items-center justify-center"><span class="loading loading-spinner loading-sm"></span></div>
      {:else if customActivity.length > 0}
        <div class="mt-4"><StatisticsChart config={customActivityChart} ariaLabel="Line chart showing custom download activity" height="22rem" /></div>
      {:else}
        <p class="mt-8 text-center text-sm text-base-content/50">No download activity in this range.</p>
      {/if}
    </section>

    <div class="mt-5 grid gap-5 xl:grid-cols-3">
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Top 10 · Most downloaded</h2>
        <div class="mt-4"><StatisticsChart config={mostDownloadedChart} ariaLabel="Polar area chart of channels with the most downloads" height="19rem" /></div>
      </section>
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Top 10 · Longest duration</h2>
        <div class="mt-4"><StatisticsChart config={longestDurationChart} ariaLabel="Polar area chart of channels with the longest downloaded duration" height="19rem" /></div>
      </section>
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Top 10 · Largest on disk</h2>
        <div class="mt-4"><StatisticsChart config={largestOnDiskChart} ariaLabel="Polar area chart of channels using the most disk space" height="19rem" /></div>
      </section>
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Top 10 · Platform types</h2>
        <p class="mt-1 text-xs text-base-content/50">Platforms grouped by available media</p>
        {#if platformTotals().length > 0}
          <div class="mt-4"><StatisticsChart config={platformTypesChart} ariaLabel="Polar area chart of the top platform types by available media" height="19rem" /></div>
        {:else}
          <p class="mt-8 text-center text-sm text-base-content/50">No platform data.</p>
        {/if}
      </section>
      <section class="card border border-base-300 bg-base-100 p-5">
        <h2 class="text-sm font-bold text-base-content">Coverage</h2>
        <p class="mt-1 text-xs text-base-content/50">Available and excluded media across the library</p>
        {#if coverageError}
          <div class="alert alert-error mt-4 text-sm" role="alert">{coverageError}</div>
        {:else if coverageLoading}
          <div class="mt-6 flex min-h-64 items-center justify-center"><span class="loading loading-spinner loading-sm"></span></div>
        {:else if coverageDetails.length > 0}
          <div class="mt-4"><StatisticsChart config={coverageChart} ariaLabel="Polar area chart of available, unavailable, ignored, and removed media" height="19rem" /></div>
        {:else}
          <p class="mt-8 text-center text-sm text-base-content/50">No coverage data.</p>
        {/if}
      </section>
    </div>

    <section class="card mt-5 border border-base-300 bg-base-100 p-5">
      <div>
        <h2 class="text-sm font-bold text-base-content">Channel statistics</h2>
        <p class="mt-1 text-xs text-base-content/50">Search for a channel to inspect its inventory and recent download health.</p>
      </div>

      <div class={['dropdown mt-4 w-full', channelSearchOpen && 'dropdown-open']}>
        <label class="input w-full">
          <Search class="h-4 w-4 text-base-content/40" />
          <input
            class="grow"
            type="text"
            bind:value={channelSearchDraft}
            placeholder="Search channel name, handle, or platform"
            autocomplete="off"
            role="combobox"
            aria-expanded={channelSearchOpen}
            aria-controls="stats-new-channel-results"
            onfocus={() => { if (channelResults.length > 0) channelSearchOpen = true; }}
            onblur={() => setTimeout(() => (channelSearchOpen = false), 150)}
            onkeydown={handleChannelSearchKeydown}
            oninput={() => {
              channelQuery = channelSearchDraft;
              scheduleChannelSearch();
            }}
          />
          {#if channelSearchLoading}<span class="loading loading-spinner loading-xs"></span>{/if}
          {#if channelQuery || selectedChannel}
            <button type="button" class="btn btn-ghost btn-xs btn-circle" aria-label="Clear selected channel" onclick={clearChannel}><X class="h-4 w-4" /></button>
          {/if}
        </label>
        {#if channelSearchOpen}
          <ul id="stats-new-channel-results" class="dropdown-content menu z-30 mt-1 max-h-72 w-full flex-nowrap overflow-y-auto rounded-lg border border-base-300 bg-base-100 p-1 shadow-xl">
            {#each channelResults as suggestion (suggestion.value)}
              <li>
                <button type="button" class="flex w-full items-center justify-between gap-3 text-left" onmousedown={(event) => event.preventDefault()} onclick={() => void chooseChannel(suggestion)}>
                  <span class="min-w-0">
                    <span class="block truncate text-sm font-semibold text-base-content">{suggestion.label}</span>
                    <span class="block truncate text-xs text-base-content/50">{suggestion.accountHandle || displayLabel(suggestion.platform)}</span>
                  </span>
                  <span class="shrink-0 text-xs text-base-content/40">{suggestion.availableCount.toLocaleString()} available</span>
                </button>
              </li>
            {:else}
              {#if !channelSearchLoading}<li><span class="text-xs text-base-content/50">No matching channels.</span></li>{/if}
            {/each}
          </ul>
        {/if}
      </div>

      {#if channelError}
        <div class="alert alert-error mt-4 text-sm" role="alert">{channelError}</div>
      {:else if channelLoading}
        <div class="mt-6 flex min-h-48 items-center justify-center"><span class="loading loading-spinner loading-sm"></span></div>
      {:else if selectedChannel}
        <div class="mt-5 flex min-w-0 items-center gap-4 rounded-xl border border-base-300 bg-base-100 p-4">
          {#if selectedChannel.summary.accountId && selectedChannel.summary.avatarStoragePath}
            <img class="h-16 w-16 shrink-0 rounded-full object-cover" src={`/api/media/watch/accounts/${selectedChannel.summary.accountId}/avatar`} alt={`${channelName(selectedChannel.summary)} avatar`} />
          {:else}
            <span class="grid h-16 w-16 shrink-0 place-items-center rounded-full bg-base-300 text-base-content/60"><Database class="h-6 w-6" /></span>
          {/if}
          <div class="min-w-0">
            <h3 class="truncate text-lg font-bold text-base-content">{channelName(selectedChannel.summary)}</h3>
            <p class="mt-1 truncate text-sm text-base-content/60">{selectedChannel.summary.accountHandle || 'No handle'}</p>
            <p class="mt-1 font-mono text-xs text-base-content/40">Account ID: {selectedChannel.summary.accountId ?? '—'}</p>
          </div>
        </div>

        <div class="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <article class="card border border-base-300 bg-base-100 p-4"><p class="text-xs text-base-content/50">Total downloaded</p><p class="mt-2 text-2xl font-bold text-base-content">{selectedChannel.summary.downloadedCount.toLocaleString()}</p></article>
          <article class="card border border-base-300 bg-base-100 p-4"><p class="text-xs text-base-content/50">Total size</p><p class="mt-2 text-2xl font-bold text-base-content">{formatBytes(selectedChannel.summary.totalBytes)}</p></article>
          <article class="card border border-base-300 bg-base-100 p-4"><p class="text-xs text-base-content/50">Downloaded duration</p><p class="mt-2 text-lg font-bold text-base-content">{formatLongDuration(selectedChannel.summary.downloadedDurationSeconds)}</p></article>
          <article class="card border border-base-300 bg-base-100 p-4"><p class="text-xs text-base-content/50">Coverage</p><p class="mt-2 text-2xl font-bold text-base-content">{Math.round(selectedChannel.summary.downloadedPercent)}%</p><p class="mt-1 text-xs text-base-content/40">{selectedChannel.summary.downloadedCount.toLocaleString()} / {selectedChannel.summary.availableCount.toLocaleString()}</p></article>
        </div>

        <div class="mt-4 grid gap-5 xl:grid-cols-3">
          <section class="card border border-base-300 bg-base-100 p-5">
            <h3 class="text-sm font-bold text-base-content">Availability</h3>
            <div class="mt-4"><StatisticsChart config={channelAvailabilityChart} ariaLabel="Pie chart showing available, unavailable, ignored, and removed channel media" height="18rem" /></div>
          </section>
          <section class="card border border-base-300 bg-base-100 p-5">
            <h3 class="text-sm font-bold text-base-content">Recent download states</h3>
            <div class="mt-4"><StatisticsChart config={channelDownloadStatesChart} ariaLabel="Pie chart showing recent channel download states" height="18rem" /></div>
          </section>
          <section class="card border border-base-300 bg-base-100 p-5">
            <h3 class="text-sm font-bold text-base-content">Media types</h3>
            <div class="mt-4"><StatisticsChart config={channelMediaTypesChart} ariaLabel="Pie chart showing channel media types" height="18rem" /></div>
          </section>
        </div>
      {/if}
    </section>
  {/if}
</section>
