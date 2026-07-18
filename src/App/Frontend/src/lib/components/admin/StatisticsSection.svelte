<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import type { ChartConfiguration } from 'chart.js';
  import { Button, Select, Spinner } from 'flowbite-svelte';
  import {
    ChartLineUpOutline,
    ChevronLeftOutline,
    ChevronRightOutline,
    ClockOutline,
    DatabaseOutline,
    DownloadOutline,
    ExclamationCircleOutline,
    EyeOutline,
    RectangleListOutline,
    RefreshOutline,
    SearchOutline
  } from 'flowbite-svelte-icons';
  import { formatBytes, formatDuration, formatRelativeDate } from '$lib/media';
  import StatisticsChart from './StatisticsChart.svelte';
  import {
    getDownloadStatistics,
    getGlobalStatistics,
    listChannelStatistics,
    suggestChannelStatistics,
    type ChannelSuggestion,
    type ChannelStatisticsSummary,
    type DownloadHistoryBucket,
    type ListChannelStatisticsOptions,
    type StatisticsBucket,
    type StatisticsOverview
  } from '$lib/api/statistics';

  type RangePreset = '30d' | '12w' | '12m' | 'custom';

  const channelPageSize = 12;
  const chartColors = ['#3b82f6', '#10b981', '#a78bfa', '#f59e0b', '#f43f5e', '#06b6d4', '#8b5cf6'];
  const sortOptions = [
    { value: 'downloaded:desc', name: 'Most downloaded' },
    { value: 'available:desc', name: 'Most available' },
    { value: 'bytes:desc', name: 'Largest on disk' },
    { value: 'duration:desc', name: 'Most duration' },
    { value: 'name:asc', name: 'Name A-Z' }
  ];
  const rangeOptions = [
    { value: '30d', name: 'Last 30 days' },
    { value: '12w', name: 'Last 12 weeks' },
    { value: '12m', name: 'Last 12 months' },
    { value: 'custom', name: 'Custom range' }
  ];
  const bucketOptions = [
    { value: 'day', name: 'Daily' },
    { value: 'week', name: 'Weekly' },
    { value: 'month', name: 'Monthly' }
  ];

  let overview = $state<StatisticsOverview | null>(null);
  let channels = $state<ChannelStatisticsSummary[]>([]);
  let downloads = $state<DownloadHistoryBucket[]>([]);
  let channelPage = $state(1);
  let totalChannels = $state(0);
  let hasMoreChannels = $state(false);
  let channelSort = $state('downloaded:desc');
  let channelSearchDraft = $state('');
  let channelSearch = $state('');
  let channelSuggestions = $state<ChannelSuggestion[]>([]);
  let channelSuggestionsLoading = $state(false);
  let channelSuggestionsOpen = $state(false);
  let activeChannelSuggestionIndex = $state(-1);
  let rangePreset = $state<RangePreset>('30d');
  let bucket = $state<StatisticsBucket>('day');
  let dateFrom = $state('');
  let dateTo = $state('');
  let loading = $state(true);
  let channelsLoading = $state(false);
  let downloadsLoading = $state(false);
  let error = $state<string | null>(null);
  let channelsError = $state<string | null>(null);
  let downloadsError = $state<string | null>(null);
  let channelSuggestionTimer: ReturnType<typeof setTimeout> | null = null;
  let channelSuggestionAbort: AbortController | null = null;
  let channelSuggestionRequestId = 0;

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

  const mediaTypesChart = $derived.by((): ChartConfiguration => ({
    type: 'doughnut',
    data: {
      labels: (overview?.mediaTypes ?? []).map((item) => displayLabel(item.type)),
      datasets: [
        {
          data: (overview?.mediaTypes ?? []).map((item) => item.count),
          backgroundColor: chartColors,
          borderColor: '#111827',
          borderWidth: 3,
          hoverOffset: 5
        }
      ]
    },
    options: doughnutOptions('Media')
  }));

  const downloadStatesChart = $derived.by((): ChartConfiguration => ({
    type: 'doughnut',
    data: {
      labels: (overview?.downloadStates ?? []).map((item) => displayLabel(item.state)),
      datasets: [
        {
          data: (overview?.downloadStates ?? []).map((item) => item.count),
          backgroundColor: ['#10b981', '#3b82f6', '#f43f5e', '#f59e0b', '#64748b', '#a78bfa'],
          borderColor: '#111827',
          borderWidth: 3,
          hoverOffset: 5
        }
      ]
    },
    options: doughnutOptions('Downloads')
  }));

  const downloadHistoryChart = $derived.by((): ChartConfiguration => ({
    type: 'line',
    data: {
      labels: downloads.map((item) => bucketLabel(item.bucketStart)),
      datasets: [
        {
          label: 'Created',
          data: downloads.map((item) => item.created),
          borderColor: '#60a5fa',
          backgroundColor: 'rgba(59, 130, 246, 0.12)',
          pointBackgroundColor: '#60a5fa',
          pointRadius: downloads.length > 45 ? 0 : 2,
          pointHoverRadius: 5,
          borderWidth: 2,
          tension: 0.3,
          fill: true
        },
        {
          label: 'Completed',
          data: downloads.map((item) => item.completed),
          borderColor: '#34d399',
          backgroundColor: 'rgba(16, 185, 129, 0.08)',
          pointBackgroundColor: '#34d399',
          pointRadius: downloads.length > 45 ? 0 : 2,
          pointHoverRadius: 5,
          borderWidth: 2,
          tension: 0.3,
          fill: true
        },
        {
          label: 'Failed',
          data: downloads.map((item) => item.failed),
          borderColor: '#fb7185',
          backgroundColor: 'rgba(244, 63, 94, 0.05)',
          pointBackgroundColor: '#fb7185',
          pointRadius: downloads.length > 45 ? 0 : 2,
          pointHoverRadius: 5,
          borderWidth: 2,
          tension: 0.3
        }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: {
          position: 'top',
          align: 'end',
          labels: { color: '#94a3b8', usePointStyle: true, pointStyle: 'circle', boxWidth: 7, boxHeight: 7 }
        },
        tooltip: {
          backgroundColor: '#0f172a',
          borderColor: '#334155',
          borderWidth: 1,
          titleColor: '#f8fafc',
          bodyColor: '#cbd5e1',
          padding: 12
        }
      },
      scales: {
        x: {
          grid: { display: false },
          ticks: { color: '#64748b', maxTicksLimit: 8, maxRotation: 0 }
        },
        y: {
          beginAtZero: true,
          grid: { color: 'rgba(51, 65, 85, 0.45)' },
          ticks: { color: '#64748b', precision: 0 }
        }
      }
    }
  }));

  onMount(() => {
    setPresetDates('30d');
    void loadAll();
  });

  onDestroy(() => {
    clearChannelSuggestionTimer();
    channelSuggestionAbort?.abort();
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
        sortOrder,
        search: channelSearch || undefined
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
    const range = selectedDateRange();
    if (!range) {
      downloadsLoading = false;
      return;
    }

    try {
      downloads = await getDownloadStatistics({ from: range.from, to: range.to, bucket });
    } catch (err) {
      downloadsError = err instanceof Error ? err.message : 'Could not load download statistics.';
    } finally {
      downloadsLoading = false;
    }
  }

  function submitChannelSearch(event: SubmitEvent) {
    event.preventDefault();
    channelSearch = channelSearchDraft.trim();
    closeChannelSuggestions();
    void loadChannels(1);
  }

  function clearChannelSearch() {
    channelSearchDraft = '';
    channelSearch = '';
    channelSuggestions = [];
    closeChannelSuggestions();
    void loadChannels(1);
  }

  function onChannelSearchInput() {
    scheduleChannelSuggestions();
  }

  function onChannelSearchFocus() {
    if (channelSuggestions.length > 0) {
      channelSuggestionsOpen = true;
      return;
    }

    scheduleChannelSuggestions(0);
  }

  function onChannelSearchKeydown(event: KeyboardEvent) {
    if (!channelSuggestionsOpen && event.key === 'ArrowDown' && channelSuggestions.length > 0) {
      channelSuggestionsOpen = true;
      activeChannelSuggestionIndex = 0;
      event.preventDefault();
      return;
    }

    if (!channelSuggestionsOpen) return;

    if (event.key === 'ArrowDown') {
      activeChannelSuggestionIndex = Math.min(channelSuggestions.length - 1, activeChannelSuggestionIndex + 1);
      event.preventDefault();
    } else if (event.key === 'ArrowUp') {
      activeChannelSuggestionIndex = Math.max(0, activeChannelSuggestionIndex - 1);
      event.preventDefault();
    } else if (event.key === 'Enter' && activeChannelSuggestionIndex >= 0) {
      applyChannelSuggestion(channelSuggestions[activeChannelSuggestionIndex]);
      event.preventDefault();
    } else if (event.key === 'Escape') {
      closeChannelSuggestions();
      event.preventDefault();
    }
  }

  function scheduleChannelSuggestions(delay = 180) {
    clearChannelSuggestionTimer();
    const query = channelSearchDraft.trim();
    if (query.length < 2) {
      channelSuggestionAbort?.abort();
      channelSuggestions = [];
      closeChannelSuggestions();
      channelSuggestionsLoading = false;
      return;
    }

    channelSuggestionTimer = setTimeout(() => void loadChannelSuggestions(query), delay);
  }

  async function loadChannelSuggestions(query: string) {
    const requestId = ++channelSuggestionRequestId;
    channelSuggestionAbort?.abort();
    const controller = new AbortController();
    channelSuggestionAbort = controller;
    channelSuggestionsLoading = true;

    try {
      const fetchWithSignal: typeof fetch = (input, init) => fetch(input, { ...init, signal: controller.signal });
      const suggestions = await suggestChannelStatistics(query, 8, fetchWithSignal);
      if (requestId !== channelSuggestionRequestId || controller.signal.aborted) return;
      channelSuggestions = suggestions;
      channelSuggestionsOpen = suggestions.length > 0 && channelSearchDraft.trim().length >= 2;
      activeChannelSuggestionIndex = suggestions.length > 0 ? 0 : -1;
    } catch (err) {
      if (controller.signal.aborted) return;
      channelSuggestions = [];
      channelSuggestionsOpen = false;
      activeChannelSuggestionIndex = -1;
    } finally {
      if (requestId === channelSuggestionRequestId) {
        channelSuggestionsLoading = false;
      }
    }
  }

  function applyChannelSuggestion(suggestion: ChannelSuggestion) {
    channelSearchDraft = suggestion.value;
    channelSearch = suggestion.value;
    closeChannelSuggestions();
    void loadChannels(1);
  }

  function closeChannelSuggestions() {
    channelSuggestionsOpen = false;
    activeChannelSuggestionIndex = -1;
  }

  function clearChannelSuggestionTimer() {
    if (channelSuggestionTimer) {
      clearTimeout(channelSuggestionTimer);
      channelSuggestionTimer = null;
    }
  }

  function changeChannelSort() {
    void loadChannels(1);
  }

  function changeRangePreset() {
    if (rangePreset !== 'custom') {
      setPresetDates(rangePreset);
      bucket = rangePreset === '30d' ? 'day' : rangePreset === '12w' ? 'week' : 'month';
      void loadDownloads();
    }
  }

  function setPresetDates(preset: Exclude<RangePreset, 'custom'>) {
    const to = new Date();
    const from = new Date(to);
    if (preset === '12m') from.setMonth(from.getMonth() - 12);
    else if (preset === '12w') from.setDate(from.getDate() - 84);
    else from.setDate(from.getDate() - 30);
    dateFrom = toDateInputValue(from);
    dateTo = toDateInputValue(to);
  }

  function selectedDateRange(): { from: Date; to: Date } | null {
    const from = new Date(`${dateFrom}T00:00:00`);
    const to = new Date(`${dateTo}T23:59:59.999`);
    if (!dateFrom || !dateTo || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
      downloadsError = 'Choose both a start and end date.';
      return null;
    }
    if (from >= to) {
      downloadsError = 'The start date must be earlier than the end date.';
      return null;
    }
    return { from, to };
  }

  function toDateInputValue(value: Date): string {
    const year = value.getFullYear();
    const month = String(value.getMonth() + 1).padStart(2, '0');
    const day = String(value.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  function channelName(channel: ChannelStatisticsSummary): string {
    return channel.accountName ?? channel.accountHandle ?? channel.sourceUrl ?? 'Unknown channel';
  }

  function displayLabel(value: string): string {
    return (value || 'Unknown')
      .replaceAll('_', ' ')
      .replace(/\b\w/g, (letter) => letter.toUpperCase());
  }

  function percent(value: number): string {
    return `${Math.round(value)}%`;
  }

  function bucketLabel(value: string): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'Unknown';
    return date.toLocaleDateString(undefined, bucket === 'month'
      ? { month: 'short', year: '2-digit' }
      : { month: 'short', day: 'numeric' });
  }

  function doughnutOptions(title: string): ChartConfiguration<'doughnut'>['options'] {
    return {
      responsive: true,
      maintainAspectRatio: false,
      cutout: '68%',
      plugins: {
        legend: {
          position: 'right',
          labels: {
            color: '#94a3b8',
            usePointStyle: true,
            pointStyle: 'circle',
            boxWidth: 7,
            boxHeight: 7,
            padding: 14,
            font: { size: 11 }
          }
        },
        tooltip: {
          backgroundColor: '#0f172a',
          borderColor: '#334155',
          borderWidth: 1,
          titleColor: '#f8fafc',
          bodyColor: '#cbd5e1',
          padding: 12,
          callbacks: {
            label: (context) => ` ${context.label}: ${Number(context.raw).toLocaleString()} ${title.toLowerCase()}`
          }
        }
      }
    };
  }
</script>

<section class="overflow-hidden rounded-3xl border border-slate-800/80 bg-[#111722] shadow-2xl shadow-black/20" aria-labelledby="statistics-title">
  <header class="border-b border-slate-800/80 bg-slate-950/25 px-5 py-6 sm:px-7">
    <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <p class="text-xs font-semibold uppercase tracking-[0.18em] text-blue-400">Library insights</p>
        <h2 id="statistics-title" class="mt-2 text-2xl font-bold tracking-tight text-white">Statistics overview</h2>
        <p class="mt-2 max-w-2xl text-sm text-slate-400">See what is in your library, how downloads are progressing, and which channels have the best coverage.</p>
      </div>
      <Button
        color="dark"
        disabled={loading || channelsLoading || downloadsLoading}
        onclick={loadAll}
        class="border-slate-700! bg-slate-950/55! px-4! py-2.5! text-sm! font-semibold! text-slate-200! hover:bg-slate-800! disabled:opacity-50"
      >
        <RefreshOutline class={['mr-2 h-4 w-4', (loading || channelsLoading || downloadsLoading) && 'animate-spin']} />
        Refresh data
      </Button>
    </div>
  </header>

  <div class="p-5 sm:p-7">
    {#if error}
      <div class="mb-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{error}</span>
      </div>
    {/if}

    {#if loading && !overview}
      <div class="flex min-h-72 items-center justify-center"><Spinner size="8" /></div>
    {:else}
      <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <article class="group relative overflow-hidden rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5">
          <div class="flex items-start justify-between">
            <div>
              <p class="text-xs font-semibold uppercase tracking-wider text-slate-500">Library media</p>
              <p class="mt-3 text-3xl font-bold tracking-tight text-white">{overview?.inventory.totalMedia.toLocaleString() ?? '-'}</p>
              <p class="mt-1 text-xs text-slate-500">Across {overview?.inventory.totalChannels.toLocaleString() ?? '-'} channels</p>
            </div>
            <span class="grid h-10 w-10 place-items-center rounded-xl bg-slate-900/80 text-slate-400"><RectangleListOutline class="h-5 w-5" /></span>
          </div>
        </article>
        <article class="relative overflow-hidden rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5">
          <div class="flex items-start justify-between">
            <div>
              <p class="text-xs font-semibold uppercase tracking-wider text-slate-500">Downloaded</p>
              <p class="mt-3 text-3xl font-bold tracking-tight text-white">{overview?.inventory.totalDownloads.toLocaleString() ?? '-'}</p>
              <p class="mt-1 text-xs text-slate-500">{overview ? formatBytes(overview.inventory.totalBytes) : '-'} stored</p>
            </div>
            <span class="grid h-10 w-10 place-items-center rounded-xl bg-slate-900/80 text-slate-400"><DownloadOutline class="h-5 w-5" /></span>
          </div>
        </article>
        <article class="relative overflow-hidden rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5">
          <div class="flex items-start justify-between">
            <div>
              <p class="text-xs font-semibold uppercase tracking-wider text-slate-500">Total runtime</p>
              <p class="mt-3 text-3xl font-bold tracking-tight text-white">{formatDuration(overview?.inventory.totalDurationSeconds) ?? '-'}</p>
              <p class="mt-1 text-xs text-slate-500">Playable media duration</p>
            </div>
            <span class="grid h-10 w-10 place-items-center rounded-xl bg-slate-900/80 text-slate-400"><ClockOutline class="h-5 w-5" /></span>
          </div>
        </article>
        <article class="relative overflow-hidden rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5">
          <div class="flex items-start justify-between">
            <div class="min-w-0 flex-1">
              <p class="text-xs font-semibold uppercase tracking-wider text-slate-500">Watched</p>
              <p class="mt-3 text-3xl font-bold tracking-tight text-white">{overview ? percent(overview.watchProgress.watchedPercent) : '-'}</p>
              <p class="mt-1 text-xs text-slate-500">{overview?.watchProgress.watchedCount.toLocaleString() ?? '-'} completed</p>
            </div>
            <span class="grid h-10 w-10 place-items-center rounded-xl bg-slate-900/80 text-slate-400"><EyeOutline class="h-5 w-5" /></span>
          </div>
        </article>
      </div>

      <div class="mt-5 grid gap-5 xl:grid-cols-2">
        <section class="rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5" aria-labelledby="media-types-title">
          <div>
            <h3 id="media-types-title" class="text-sm font-bold text-slate-100">Media mix</h3>
            <p class="mt-1 text-xs text-slate-500">How your library is distributed by media type</p>
          </div>
          {#if (overview?.mediaTypes.length ?? 0) === 0}
            <p class="mt-8 text-center text-sm text-slate-500">No media type statistics yet.</p>
          {:else}
            <div class="mt-4"><StatisticsChart config={mediaTypesChart} ariaLabel="Doughnut chart of media types in the library" height="15rem" /></div>
          {/if}
        </section>

        <section class="rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5" aria-labelledby="download-states-title">
          <div>
            <h3 id="download-states-title" class="text-sm font-bold text-slate-100">Download health</h3>
            <p class="mt-1 text-xs text-slate-500">Current job totals grouped by state</p>
          </div>
          {#if (overview?.downloadStates.length ?? 0) === 0}
            <p class="mt-8 text-center text-sm text-slate-500">No download state statistics yet.</p>
          {:else}
            <div class="mt-4"><StatisticsChart config={downloadStatesChart} ariaLabel="Doughnut chart of download job states" height="15rem" /></div>
          {/if}
        </section>
      </div>

      <section class="mt-5 rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5" aria-labelledby="download-history-title">
        <div class="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div>
            <h3 id="download-history-title" class="text-sm font-bold text-slate-100">Download activity</h3>
            <p class="mt-1 text-xs text-slate-500">Created, completed, and failed jobs over time</p>
          </div>
          <div class="flex flex-wrap items-end gap-2">
            <label class="block">
              <span class="mb-1.5 block text-[11px] font-semibold uppercase tracking-wide text-slate-600">Range</span>
              <Select
                items={rangeOptions}
                bind:value={rangePreset}
                onchange={changeRangePreset}
                aria-label="Download history date range"
                class="w-40! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500!"
              />
            </label>
            <label class="block">
              <span class="mb-1.5 block text-[11px] font-semibold uppercase tracking-wide text-slate-600">Interval</span>
              <Select
                items={bucketOptions}
                bind:value={bucket}
                onchange={() => void loadDownloads()}
                aria-label="Download history chart interval"
                class="w-32! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500!"
              />
            </label>
          </div>
        </div>

        {#if rangePreset === 'custom'}
          <div class="mt-4 flex flex-wrap items-end gap-3 rounded-xl border border-slate-800/70 bg-slate-900/45 p-3">
            <label class="block">
              <span class="mb-1.5 block text-xs font-semibold text-slate-400">From</span>
              <input type="date" bind:value={dateFrom} max={dateTo || undefined} class="rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-sm text-slate-200 focus:border-blue-500 focus:ring-1 focus:ring-blue-500" />
            </label>
            <label class="block">
              <span class="mb-1.5 block text-xs font-semibold text-slate-400">To</span>
              <input type="date" bind:value={dateTo} min={dateFrom || undefined} max={toDateInputValue(new Date())} class="rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-sm text-slate-200 focus:border-blue-500 focus:ring-1 focus:ring-blue-500" />
            </label>
            <Button color="blue" onclick={loadDownloads} class="px-4! py-2! text-sm! font-semibold!">Apply range</Button>
          </div>
        {/if}

        <div class="mt-5 grid gap-3 sm:grid-cols-3">
          <div class="rounded-xl border border-slate-800/70 bg-slate-900/40 px-4 py-3">
            <p class="text-xs text-slate-500">Jobs created</p>
            <p class="mt-1 text-xl font-bold text-slate-100">{downloadTotals.created.toLocaleString()}</p>
          </div>
          <div class="rounded-xl border border-slate-800/70 bg-slate-900/40 px-4 py-3">
            <p class="text-xs text-slate-500">Completed</p>
            <p class="mt-1 text-xl font-bold text-slate-100">{downloadTotals.completed.toLocaleString()}</p>
          </div>
          <div class="rounded-xl border border-slate-800/70 bg-slate-900/40 px-4 py-3">
            <p class="text-xs text-slate-500">Data completed</p>
            <p class="mt-1 text-xl font-bold text-slate-100">{formatBytes(downloadTotals.bytesCompleted)}</p>
          </div>
        </div>

        {#if downloadsError}
          <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
            <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
            <span>{downloadsError}</span>
          </div>
        {:else if downloadsLoading}
          <div class="mt-8 flex min-h-64 items-center justify-center"><Spinner size="6" /></div>
        {:else if downloads.length === 0}
          <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-10 text-center">
            <ChartLineUpOutline class="mx-auto h-9 w-9 text-slate-700" />
            <p class="mt-4 text-sm font-semibold text-slate-300">No activity in this date range</p>
            <p class="mt-1 text-xs text-slate-600">Try a wider range or another interval.</p>
          </div>
        {:else}
          <div class="mt-5"><StatisticsChart config={downloadHistoryChart} ariaLabel="Line chart of created, completed, and failed download jobs over time" height="21rem" /></div>
        {/if}
      </section>

      <section class="mt-5 rounded-2xl border border-slate-800/80 bg-slate-950/25 p-5" aria-labelledby="channel-stats-title">
        <div class="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
          <div>
            <h3 id="channel-stats-title" class="text-sm font-bold text-slate-100">Channel coverage</h3>
            <p class="mt-1 text-xs text-slate-500">
              {#if channelSearch}
                {totalChannels.toLocaleString()} results for “{channelSearch}”
              {:else}
                {totalChannels.toLocaleString()} channels with media in your library
              {/if}
            </p>
          </div>
          <div class="flex flex-col gap-2 sm:flex-row sm:items-center">
            <form class="min-w-0 sm:w-[28rem] xl:w-[34rem]" onsubmit={submitChannelSearch} role="search">
              <label class="relative min-w-0 flex-1">
                <span class="sr-only">Search channels</span>
                <SearchOutline class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-600" />
                <input
                  type="search"
                  bind:value={channelSearchDraft}
                  placeholder="Search name, handle, platform…"
                  autocomplete="off"
                  role="combobox"
                  aria-autocomplete="list"
                  aria-haspopup="listbox"
                  aria-controls="channel-search-suggestions"
                  aria-expanded={channelSuggestionsOpen}
                  aria-activedescendant={activeChannelSuggestionIndex >= 0 ? `channel-search-suggestion-${activeChannelSuggestionIndex}` : undefined}
                  oninput={onChannelSearchInput}
                  onfocus={onChannelSearchFocus}
                  onblur={() => setTimeout(closeChannelSuggestions, 150)}
                  onkeydown={onChannelSearchKeydown}
                  class="w-full rounded-lg border border-slate-800 bg-slate-900/80 py-2.5 pl-9 pr-8 text-sm text-slate-200 placeholder:text-slate-600 focus:z-10 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                />
                {#if channelSuggestionsLoading}
                  <div class="absolute right-2 top-1/2 -translate-y-1/2"><Spinner size="4" /></div>
                {/if}
                {#if channelSuggestionsOpen}
                  <div
                    id="channel-search-suggestions"
                    class="absolute left-0 right-0 top-full z-30 mt-1 max-h-72 overflow-y-auto rounded-lg border border-slate-700 bg-[#10141e] shadow-xl shadow-black/40"
                    role="listbox"
                    aria-label="Channel suggestions"
                  >
                    {#each channelSuggestions as suggestion, index (`${suggestion.platform}:${suggestion.value}`)}
                      <button
                        id={`channel-search-suggestion-${index}`}
                        type="button"
                        role="option"
                        aria-selected={activeChannelSuggestionIndex === index}
                        class={[
                          'flex w-full items-center gap-2 px-3 py-2 text-left transition',
                          activeChannelSuggestionIndex === index ? 'bg-blue-500/15' : 'hover:bg-blue-500/10'
                        ]}
                        onmousedown={(event) => event.preventDefault()}
                        onclick={() => applyChannelSuggestion(suggestion)}
                        onmouseenter={() => (activeChannelSuggestionIndex = index)}
                      >
                        <span class="min-w-0 flex-1 truncate text-sm font-semibold text-slate-100">{suggestion.label}</span>
                        {#if suggestion.accountHandle}
                          <span class="min-w-0 truncate text-xs text-slate-500">{suggestion.accountHandle}</span>
                        {/if}
                        <span class="shrink-0 text-xs text-slate-600">{displayLabel(suggestion.platform)}</span>
                      </button>
                    {/each}
                  </div>
                {/if}
              </label>
            </form>
            {#if channelSearch}
              <button type="button" onclick={clearChannelSearch} class="px-2 py-2 text-xs font-semibold text-slate-500 transition hover:text-slate-300">Clear</button>
            {/if}
            <Select
              items={sortOptions}
              bind:value={channelSort}
              onchange={changeChannelSort}
              aria-label="Sort channel statistics"
              class="w-full! border-slate-800! bg-slate-900/80! text-sm! text-slate-300! focus:border-blue-500! focus:ring-blue-500! sm:w-48!"
            />
          </div>
        </div>

        {#if channelsError}
          <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
            <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
            <span>{channelsError}</span>
          </div>
        {:else if channelsLoading}
          <div class="mt-8 flex min-h-48 items-center justify-center"><Spinner size="6" /></div>
        {:else if channels.length === 0}
          <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-10 text-center">
            {#if channelSearch}
              <SearchOutline class="mx-auto h-9 w-9 text-slate-700" />
              <p class="mt-4 text-sm font-semibold text-slate-300">No matching channels</p>
              <p class="mt-1 text-xs text-slate-600">Try a channel name, handle, or platform.</p>
            {:else}
              <DatabaseOutline class="mx-auto h-9 w-9 text-slate-700" />
              <p class="mt-4 text-sm font-semibold text-slate-300">No channel statistics</p>
            {/if}
          </div>
        {:else}
          <div class="mt-5 overflow-x-auto">
            <table class="min-w-full text-left text-sm">
              <thead class="border-y border-slate-800/80 bg-slate-900/30 text-[11px] uppercase tracking-wider text-slate-600">
                <tr>
                  <th class="px-3 py-3 font-semibold">Channel</th>
                  <th class="px-4 py-3 font-semibold">Coverage</th>
                  <th class="px-4 py-3 font-semibold">Downloaded runtime</th>
                  <th class="px-4 py-3 font-semibold">Storage</th>
                  <th class="px-3 py-3 font-semibold">Last scan</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-800/60">
                {#each channels as channel (channel.accountId ?? channel.creatorSourceId ?? channel.sourceUrl)}
                  <tr class="text-slate-300 transition hover:bg-slate-900/35">
                    <td class="px-3 py-4">
                      <div class="flex min-w-56 items-center gap-3">
                        <span class="grid h-10 w-10 shrink-0 place-items-center rounded-xl border border-slate-700/60 bg-slate-800/60 text-blue-400">
                          <DatabaseOutline class="h-4.5 w-4.5" />
                        </span>
                        <div class="min-w-0">
                          {#if channel.accountId}
                            <a href={`/channel/${channel.accountId}`} class="block truncate font-semibold text-slate-100 hover:text-blue-300">{channelName(channel)}</a>
                          {:else}
                            <p class="truncate font-semibold text-slate-100">{channelName(channel)}</p>
                          {/if}
                          <p class="mt-0.5 truncate text-xs text-slate-500">{displayLabel(channel.platform)}{channel.sourceType ? ` · ${channel.sourceType}` : ''}</p>
                        </div>
                      </div>
                    </td>
                    <td class="px-4 py-4">
                      <div class="min-w-36">
                        <div class="flex items-center justify-between gap-3">
                          <span class="font-semibold text-slate-100">{percent(channel.downloadedPercent)}</span>
                          <span class="text-xs text-slate-600">{channel.downloadedCount.toLocaleString()} / {channel.availableCount.toLocaleString()}</span>
                        </div>
                        <div class="mt-2 h-1.5 overflow-hidden rounded-full bg-slate-800">
                          <div class="h-full rounded-full bg-gradient-to-r from-blue-500 to-emerald-400" style={`width: ${Math.min(100, channel.downloadedPercent)}%`}></div>
                        </div>
                      </div>
                    </td>
                    <td class="px-4 py-4 text-slate-400">{formatDuration(channel.downloadedDurationSeconds) ?? '-'}</td>
                    <td class="px-4 py-4 text-slate-400">{formatBytes(channel.totalBytes)}</td>
                    <td class="px-3 py-4 text-slate-500">{formatRelativeDate(channel.lastSuccessfulScanAt) ?? 'Never'}</td>
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
  </div>
</section>
