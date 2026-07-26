<script lang="ts">
  import { onMount } from 'svelte';
  import { Modal, Select } from '$lib/components/ui';
  import {
    ChevronDown,
    CircleAlert,
    Clock,
    Info,
    Pencil,
    RefreshCw,
    Server,
    X
  } from '@lucide/svelte';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
  import { ApiRequestError } from '$lib/api/http';
  import {
    listSchedules,
    scheduleTaskTypes,
    scheduleTimingSummary,
    updateSchedule,
    type ScheduleCatchupPolicy,
    type ScheduledTask
  } from '$lib/api/schedules';

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';

  const taskTypeItems = scheduleTaskTypes.map((taskType) => ({ value: taskType, name: taskType }));
  const timingItems = [
    { value: 'interval', name: 'Interval' },
    { value: 'cron', name: 'Cron expression' }
  ];
  const catchupItems: { value: ScheduleCatchupPolicy; name: string }[] = [
    { value: 'Coalesce', name: 'Coalesce — run once to catch up' },
    { value: 'Skip', name: 'Skip — wait for the next occurrence' }
  ];
  const secondItems = Array.from({ length: 60 }, (_, value) => ({ value: String(value), name: String(value).padStart(2, '0') }));
  const minuteItems = [
    ...secondItems,
    { value: '*/5', name: 'Every 5 minutes' },
    { value: '*/10', name: 'Every 10 minutes' },
    { value: '*/15', name: 'Every 15 minutes' },
    { value: '*/30', name: 'Every 30 minutes' }
  ];
  const hourItems = [
    ...Array.from({ length: 24 }, (_, value) => ({ value: String(value), name: String(value).padStart(2, '0') })),
    { value: '*/2', name: 'Every 2 hours' },
    { value: '*/4', name: 'Every 4 hours' },
    { value: '*/6', name: 'Every 6 hours' },
    { value: '*/12', name: 'Every 12 hours' }
  ];
  const dayOfMonthItems = [
    { value: '*', name: 'Every day' },
    { value: '?', name: 'No specific day' },
    ...Array.from({ length: 31 }, (_, index) => ({ value: String(index + 1), name: String(index + 1) }))
  ];
  const monthItems = [
    { value: '*', name: 'Every month' },
    { value: 'JAN', name: 'January' },
    { value: 'FEB', name: 'February' },
    { value: 'MAR', name: 'March' },
    { value: 'APR', name: 'April' },
    { value: 'MAY', name: 'May' },
    { value: 'JUN', name: 'June' },
    { value: 'JUL', name: 'July' },
    { value: 'AUG', name: 'August' },
    { value: 'SEP', name: 'September' },
    { value: 'OCT', name: 'October' },
    { value: 'NOV', name: 'November' },
    { value: 'DEC', name: 'December' }
  ];
  const dayOfWeekItems = [
    { value: '?', name: 'No specific weekday' },
    { value: '*', name: 'Every weekday' },
    { value: 'MON', name: 'Monday' },
    { value: 'TUE', name: 'Tuesday' },
    { value: 'WED', name: 'Wednesday' },
    { value: 'THU', name: 'Thursday' },
    { value: 'FRI', name: 'Friday' },
    { value: 'SAT', name: 'Saturday' },
    { value: 'SUN', name: 'Sunday' }
  ];

  const taskTypeHelp = [
    {
      type: 'channel_scan_refresh',
      summary: 'Checks followed channels for new uploads and queues any newly discovered media.'
    },
    {
      type: 'channel_asset_refresh',
      summary: 'Refreshes channel-level assets such as avatars and banners.'
    },
    {
      type: 'channel_scan_full',
      summary: 'Rebuilds the channel media listing used by the library and creator views.'
    },
    {
      type: 'database_stale_media_cleanup',
      summary: 'Cleans up stale maintenance records and other aged scheduler data.'
    },
    {
      type: 'database_maintenance',
      summary: 'Runs routine database maintenance work such as housekeeping and compaction.'
    },
    {
      type: 'database_maintenance_reindex',
      summary: 'Rebuilds all indexes in the PostgreSQL database using concurrent reindexing.'
    },
    {
      type: 'search_reindex',
      summary: 'Rebuilds the search index from the authoritative metadata store.'
    },
    {
      type: 'processed_message_cleanup',
      summary: 'Removes old processed-message records so the job history stays small.'
    },
    {
      type: 'backup',
      summary: 'Runs the configured backup workflow for the current deployment.'
    }
  ] as const;

  function taskTypeSummary(taskType: string): string {
    return taskTypeHelp.find((item) => item.type === taskType)?.summary ?? 'No description available.';
  }

  let schedules = $state<ScheduledTask[]>([]);
  let loading = $state(true);
  let loadError = $state<Error | null>(null);
  let mutation = $state<string | null>(null);

  let formOpen = $state(false);
  let editingKey = $state<string | null>(null);
  let formError = $state<string | null>(null);
  let formSaving = $state(false);

  let formKey = $state('');
  let formTaskType = $state<string>(scheduleTaskTypes[0]);
  let formTiming = $state<'cron' | 'interval'>('interval');
  let formCron = $state('');
  let formIntervalSeconds = $state<number | string>(3600);
  let formTimezone = $state('UTC');
  let formEnabled = $state(true);
  let formCatchupPolicy = $state<ScheduleCatchupPolicy>('Coalesce');
  let cronSecond = $state('0');
  let cronMinute = $state('0');
  let cronHour = $state('3');
  let cronDayOfMonth = $state('*');
  let cronMonth = $state('*');
  let cronDayOfWeek = $state('?');

  let taskTypeHelpOpen = $state(false);
  const cronBuilderExpression = $derived(`${cronSecond} ${cronMinute} ${cronHour} ${cronDayOfMonth} ${cronMonth} ${cronDayOfWeek}`);

  const bridgeUnavailable = $derived(loadError instanceof ApiRequestError && loadError.status === 503);

  onMount(() => {
    void load();
  });

  async function load() {
    loading = true;
    loadError = null;
    try {
      schedules = (await listSchedules()).sort((a, b) => a.key.localeCompare(b.key));
    } catch (err) {
      loadError = err instanceof Error ? err : new Error('Could not load schedules.');
    } finally {
      loading = false;
    }
  }

  function openEditForm(schedule: ScheduledTask) {
    editingKey = schedule.key;
    formKey = schedule.key;
    formTaskType = schedule.taskType;
    formTiming = schedule.cron ? 'cron' : 'interval';
    formCron = schedule.cron ?? '';
    formIntervalSeconds = schedule.intervalSeconds ?? 3600;
    formTimezone = schedule.timezone;
    formEnabled = schedule.enabled;
    formCatchupPolicy = schedule.catchupPolicy;
    syncCronBuilderFromExpression(formCron);
    formError = null;
    formOpen = true;
  }

  async function saveForm(event: SubmitEvent) {
    event.preventDefault();
    formError = null;

    const cron = formTiming === 'cron' ? formCron.trim() : '';
    const intervalSeconds = formTiming === 'interval' ? Number(formIntervalSeconds) : null;
    if (formTiming === 'cron' && !cron) {
      formError = 'Enter a Quartz cron expression.';
      return;
    }
    if (formTiming === 'interval' && (!Number.isInteger(intervalSeconds) || (intervalSeconds ?? 0) < 1)) {
      formError = 'Interval must be a whole number of seconds, 1 or greater.';
      return;
    }

    const request = {
      taskType: formTaskType,
      cron: cron || null,
      intervalSeconds,
      timezone: formTimezone.trim() || 'UTC',
      enabled: formEnabled,
      catchupPolicy: formCatchupPolicy
    };

    formSaving = true;
    try {
      await updateSchedule(editingKey ?? formKey, request);
      formOpen = false;
      await load();
    } catch (err) {
      formError = err instanceof Error ? err.message : 'Could not save the schedule.';
    } finally {
      formSaving = false;
    }
  }

  async function toggleEnabled(schedule: ScheduledTask) {
    mutation = `toggle:${schedule.key}`;
    loadError = null;
    try {
      const updated = await updateSchedule(schedule.key, {
        taskType: schedule.taskType,
        cron: schedule.cron,
        intervalSeconds: schedule.intervalSeconds,
        timezone: schedule.timezone,
        enabled: !schedule.enabled,
        catchupPolicy: schedule.catchupPolicy
      });
      schedules = schedules.map((item) => (item.key === schedule.key ? updated : item));
    } catch (err) {
      loadError = err instanceof Error ? err : new Error('Could not update the schedule.');
    } finally {
      mutation = null;
    }
  }

  function formatDate(value: string | null): string {
    if (!value) {
      return 'never';
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'unknown' : date.toLocaleString();
  }

  function syncCronBuilderFromExpression(expression: string) {
    const parts = expression.trim().split(/\s+/);
    if (parts.length < 6) {
      return;
    }

    cronSecond = parts[0];
    cronMinute = parts[1];
    cronHour = parts[2];
    cronDayOfMonth = parts[3];
    cronMonth = parts[4];
    cronDayOfWeek = parts[5];
  }

  function updateCronExpression() {
    formCron = cronBuilderExpression;
  }

  function handleCronDayOfMonthChange() {
    if (cronDayOfMonth !== '?') {
      cronDayOfWeek = '?';
    }
    updateCronExpression();
  }

  function handleCronDayOfWeekChange() {
    if (cronDayOfWeek !== '?') {
      cronDayOfMonth = '?';
    }
    updateCronExpression();
  }
</script>

<UnderDevelopmentBanner />

<section class={cardClass} aria-labelledby="schedules-title">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="min-w-0">
      <div class="flex items-center gap-2">
        <Clock class="h-5 w-5 text-primary" />
        <h2 id="schedules-title" class="text-base font-bold text-base-content">Schedules</h2>
      </div>
      <p class="mt-2 text-sm text-base-content/60">
        Recurring background tasks — metadata cleanup, channel checks, backups, and other maintenance jobs.
      </p>
    </div>
    <div class="flex shrink-0 gap-2">
      <button class="btn btn-sm btn-neutral" disabled={loading} onclick={() => void load()}>
        <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </button>
    </div>
  </div>

  {#if bridgeUnavailable}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-warning/60 bg-warning/10 p-3 text-sm text-warning"
      role="alert"
    >
      <Server class="mt-0.5 h-4 w-4 shrink-0" />
      <span>DataBridge is unreachable. Schedule operations route through DataBridge/NATS and cannot complete until it recovers.</span>
    </div>
  {:else if loadError}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{loadError.message}</span>
    </div>
  {/if}

  {#if formOpen}
    <form class="mt-5 space-y-4 rounded-xl border border-base-300/80 bg-base-200/25 p-4" onsubmit={saveForm}>
      <div class="flex items-center justify-between gap-3">
        <h3 class="text-sm font-bold text-base-content">Edit schedule "{editingKey}"</h3>
        <button
          type="button"
          class="grid h-8 w-8 place-items-center rounded-lg text-base-content/60 hover:bg-base-300 hover:text-base-content"
          aria-label="Close form"
          onclick={() => (formOpen = false)}
        >
          <X class="h-4 w-4" />
        </button>
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="schedule-key">Key</label>
          <input class="input w-full" id="schedule-key" required
             pattern={'[a-z0-9-]{2,100}'} minlength={2} maxlength={100} disabled bind:value={formKey} />
        </div>
        <div>
          <div class="mb-2 flex items-center gap-1.5">
            <label class="label text-sm" for="schedule-task-type">Task type</label>
            <button
              type="button"
              class="inline-flex h-5 w-5 items-center justify-center rounded-full text-base-content/50 transition hover:text-base-content/90"
              aria-label="Explain task types"
              title="Explain task types"
              onclick={() => (taskTypeHelpOpen = true)}
            >
              <Info class="h-4 w-4" />
            </button>
          </div>
          <Select id="schedule-task-type" items={taskTypeItems} bind:value={formTaskType} disabled />
        </div>
      </div>

      <details open class="group rounded-xl border border-base-300/70 bg-base-200/40 p-4">
        <summary class="flex cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
          <h3 class="text-sm font-semibold text-base-content/90">Timing</h3>
          <ChevronDown class="h-4 w-4 shrink-0 text-base-content/50 transition-transform group-open:rotate-180" />
        </summary>
        <div class="mt-4 grid gap-4 sm:grid-cols-3">
          <div>
            <label class="label mb-2 text-sm" for="schedule-timing">Timing mode</label>
            <Select id="schedule-timing" items={timingItems} bind:value={formTiming} />
          </div>
          {#if formTiming === 'cron'}
            <div class="sm:col-span-2">
              <label class="label mb-2 text-sm" for="schedule-cron">Cron expression</label>
              <input class="input w-full font-mono" id="schedule-cron" bind:value={formCron} placeholder="0 0 3 * * ?" />
              <p class="mt-1.5 text-xs text-base-content/40">Quartz format: seconds minutes hours day-of-month month day-of-week.</p>
            </div>
            <div class="sm:col-span-3 rounded-lg border border-base-300/80 bg-base-100 p-3">
              <div class="grid gap-3 sm:grid-cols-3 lg:grid-cols-6">
                <div>
                  <label class="label mb-1.5 text-xs" for="cron-second">Seconds</label>
                  <Select id="cron-second" items={secondItems} bind:value={cronSecond} onchange={updateCronExpression} />
                </div>
                <div>
                  <label class="label mb-1.5 text-xs" for="cron-minute">Minutes</label>
                  <Select id="cron-minute" items={minuteItems} bind:value={cronMinute} onchange={updateCronExpression} />
                </div>
                <div>
                  <label class="label mb-1.5 text-xs" for="cron-hour">Hours</label>
                  <Select id="cron-hour" items={hourItems} bind:value={cronHour} onchange={updateCronExpression} />
                </div>
                <div>
                  <label class="label mb-1.5 text-xs" for="cron-day-month">Day</label>
                  <Select id="cron-day-month" items={dayOfMonthItems} bind:value={cronDayOfMonth} onchange={handleCronDayOfMonthChange} />
                </div>
                <div>
                  <label class="label mb-1.5 text-xs" for="cron-month">Month</label>
                  <Select id="cron-month" items={monthItems} bind:value={cronMonth} onchange={updateCronExpression} />
                </div>
                <div>
                  <label class="label mb-1.5 text-xs" for="cron-day-week">Weekday</label>
                  <Select id="cron-day-week" items={dayOfWeekItems} bind:value={cronDayOfWeek} onchange={handleCronDayOfWeekChange} />
                </div>
              </div>
              <div class="mt-3 flex flex-wrap items-center justify-between gap-2">
                <span class="text-xs text-base-content/50">Generated expression</span>
                <code class="rounded bg-base-200 px-2 py-1 font-mono text-xs text-base-content/70">{cronBuilderExpression}</code>
              </div>
            </div>
          {:else}
            <div class="sm:col-span-2">
              <label class="label mb-2 text-sm" for="schedule-interval">Interval (seconds)</label>
              <input class="input w-full" id="schedule-interval" type="number" min={1} bind:value={formIntervalSeconds} placeholder="3600" />
            </div>
          {/if}
        </div>
      </details>

      <div class="grid gap-4 sm:grid-cols-3">
        <div>
          <label class="label mb-2 text-sm" for="schedule-timezone">Timezone</label>
          <input class="input w-full" id="schedule-timezone" required  bind:value={formTimezone} placeholder="UTC" />
          <p class="mt-1.5 text-xs text-base-content/40">TZDB id, e.g. UTC or America/Los_Angeles.</p>
        </div>
        <div>
          <label class="label mb-2 text-sm" for="schedule-catchup">Missed-run policy</label>
          <Select id="schedule-catchup" items={catchupItems} bind:value={formCatchupPolicy} />
        </div>
        <div class="flex items-end pb-2">
          <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="toggle" bind:checked={formEnabled} /><span>Enabled</span></label>
        </div>
      </div>

      {#if formError}
        <div class="flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error" role="alert">
          <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{formError}</span>
        </div>
      {/if}

      <div class="flex justify-end gap-2">
        <button class="btn btn-sm btn-neutral" onclick={() => (formOpen = false)}>Cancel</button>
        <button class="btn btn-sm btn-primary" type="submit" disabled={formSaving}>
          {#if formSaving}
            <span class="loading loading-spinner loading-xs mr-1.5"></span>
          {/if}
          Save changes
        </button>
      </div>
    </form>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center"><span class="loading loading-spinner loading-md"></span></div>
  {:else if schedules.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <Clock class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No schedules yet</p>
      <p class="mt-1 text-sm text-base-content/50">Run migrations to seed the registered scheduler task types.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each schedules as schedule (schedule.key)}
        <article
          class="flex flex-col gap-3 rounded-lg border border-base-content/20 bg-base-100 px-3 py-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:px-4 lg:flex-row lg:items-center"
        >
          <div class="flex min-w-0 flex-1 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
              <Clock class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-base-content">{schedule.key}</h3>
                <span class="rounded-full bg-base-300 px-2 py-0.5 font-mono text-[10px] font-semibold text-base-content/60">
                  {schedule.taskType}
                </span>
                {#if !schedule.enabled}
                  <span class="rounded-full bg-warning/10 px-2 py-0.5 text-[10px] font-semibold text-warning">disabled</span>
                {/if}
              </div>
              <p class="mt-1 text-xs leading-relaxed text-base-content/60">{taskTypeSummary(schedule.taskType)}</p>
              <p class="mt-0.5 truncate font-mono text-xs text-base-content/60">
                {scheduleTimingSummary(schedule)} · {schedule.timezone} · {schedule.catchupPolicy === 'Coalesce' ? 'coalesce missed runs' : 'skip missed runs'}
              </p>
              <p class="mt-0.5 truncate text-xs text-base-content/50">
                Next due {formatDate(schedule.nextDueAt)} · last success {formatDate(schedule.lastSuccessAt)} · last attempt {formatDate(schedule.lastAttemptAt)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 items-center gap-2 lg:ml-auto">
            {#if mutation === `toggle:${schedule.key}`}
              <span class="loading loading-spinner loading-sm"></span>
            {:else}
              <input type="checkbox" class="toggle" checked={schedule.enabled} disabled={mutation !== null} aria-label={`${schedule.enabled ? 'Disable' : 'Enable'} schedule ${schedule.key}`} onchange={() => void toggleEnabled(schedule)} />
            {/if}
            <button
              type="button"
              class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-base-content/20 bg-base-200/70 px-3 text-base-content/80 transition hover:border-primary/60 hover:bg-primary/10 hover:text-primary"
              title="Edit schedule"
              aria-label={`Edit schedule ${schedule.key}`}
              onclick={() => openEditForm(schedule)}
            >
              <Pencil class="h-4 w-4" />
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}
</section>

<Modal bind:open={taskTypeHelpOpen} title="Task type help" size="lg">
  <div class="space-y-4">
    <p class="text-sm text-base-content/80">
      Each schedule runs one registered background job. The task type determines what the scheduler queues when the schedule fires.
    </p>
    <div class="space-y-3">
      {#each taskTypeHelp as item}
        <div class="rounded-xl border border-base-300/80 bg-base-200/30 p-3">
          <p class="font-mono text-xs font-semibold text-primary">{item.type}</p>
          <p class="mt-1 text-sm text-base-content/80">{item.summary}</p>
        </div>
      {/each}
    </div>
  </div>
</Modal>
