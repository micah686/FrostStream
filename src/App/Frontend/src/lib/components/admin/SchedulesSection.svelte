<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Label, Select, Spinner, Toggle } from 'flowbite-svelte';
  import {
    ClockOutline,
    CloseOutline,
    EditOutline,
    ExclamationCircleOutline,
    PlusOutline,
    RefreshOutline,
    ServerOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import { ApiRequestError } from '$lib/api/http';
  import {
    createSchedule,
    deleteSchedule,
    listSchedules,
    scheduleTaskTypes,
    scheduleTimingSummary,
    updateSchedule,
    type ScheduleCatchupPolicy,
    type ScheduledTask
  } from '$lib/api/schedules';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';
  const saveButtonClass = 'border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60';

  const taskTypeItems = scheduleTaskTypes.map((taskType) => ({ value: taskType, name: taskType }));
  const timingItems = [
    { value: 'interval', name: 'Interval' },
    { value: 'cron', name: 'Cron expression' }
  ];
  const catchupItems: { value: ScheduleCatchupPolicy; name: string }[] = [
    { value: 'Coalesce', name: 'Coalesce — run once to catch up' },
    { value: 'Skip', name: 'Skip — wait for the next occurrence' }
  ];

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

  let deleteTarget = $state<ScheduledTask | null>(null);
  let deleteModalOpen = $state(false);

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

  function openCreateForm() {
    editingKey = null;
    formKey = '';
    formTaskType = scheduleTaskTypes[0];
    formTiming = 'interval';
    formCron = '';
    formIntervalSeconds = 3600;
    formTimezone = 'UTC';
    formEnabled = true;
    formCatchupPolicy = 'Coalesce';
    formError = null;
    formOpen = true;
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
      if (editingKey) {
        await updateSchedule(editingKey, request);
      } else {
        await createSchedule({ key: formKey.trim(), ...request });
      }
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

  async function removeSchedule(schedule: ScheduledTask) {
    mutation = `delete:${schedule.key}`;
    loadError = null;
    try {
      await deleteSchedule(schedule.key);
      schedules = schedules.filter((item) => item.key !== schedule.key);
      deleteTarget = null;
      deleteModalOpen = false;
    } catch (err) {
      loadError = err instanceof Error ? err : new Error('Could not delete the schedule.');
      throw err;
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
</script>

<section class={cardClass} aria-labelledby="schedules-title">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="min-w-0">
      <div class="flex items-center gap-2">
        <ClockOutline class="h-5 w-5 text-blue-400" />
        <h2 id="schedules-title" class="text-base font-bold text-slate-100">Schedules</h2>
      </div>
      <p class="mt-2 text-sm text-slate-400">
        Recurring background tasks — metadata cleanup, channel checks, backups, and other maintenance jobs.
      </p>
    </div>
    <div class="flex shrink-0 gap-2">
      <Button color="dark" class={outlineButtonClass} disabled={loading} onclick={() => void load()}>
        <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </Button>
      <Button color="blue" class={saveButtonClass} onclick={openCreateForm}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        New schedule
      </Button>
    </div>
  </div>

  {#if bridgeUnavailable}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-amber-700/60 bg-amber-950/30 p-3 text-sm text-amber-200"
      role="alert"
    >
      <ServerOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>DataBridge is unreachable. Schedule operations route through DataBridge/NATS and cannot complete until it recovers.</span>
    </div>
  {:else if loadError}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{loadError.message}</span>
    </div>
  {/if}

  {#if formOpen}
    <form class="mt-5 space-y-4 rounded-xl border border-slate-800/80 bg-slate-950/25 p-4" onsubmit={saveForm}>
      <div class="flex items-center justify-between gap-3">
        <h3 class="text-sm font-bold text-slate-100">{editingKey ? `Edit schedule "${editingKey}"` : 'New schedule'}</h3>
        <button
          type="button"
          class="grid h-8 w-8 place-items-center rounded-lg text-slate-400 hover:bg-slate-800 hover:text-slate-100"
          aria-label="Close form"
          onclick={() => (formOpen = false)}
        >
          <CloseOutline class="h-4 w-4" />
        </button>
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <div>
          <Label for="schedule-key" class="mb-2 text-sm font-medium text-slate-300">Key</Label>
          <Input
            id="schedule-key"
            required
            pattern={'[a-z0-9-]{2,100}'}
            minlength={2}
            maxlength={100}
            disabled={editingKey !== null}
            bind:value={formKey}
            placeholder="nightly-backup"
            class="{inputClass} disabled:opacity-60"
          />
          <p class="mt-1.5 text-xs text-slate-600">Lowercase letters, numbers, and hyphens.</p>
        </div>
        <div>
          <Label for="schedule-task-type" class="mb-2 text-sm font-medium text-slate-300">Task type</Label>
          <Select id="schedule-task-type" items={taskTypeItems} bind:value={formTaskType} class={inputClass} />
        </div>
      </div>

      <div class="grid gap-4 sm:grid-cols-3">
        <div>
          <Label for="schedule-timing" class="mb-2 text-sm font-medium text-slate-300">Timing</Label>
          <Select id="schedule-timing" items={timingItems} bind:value={formTiming} class={inputClass} />
        </div>
        {#if formTiming === 'cron'}
          <div class="sm:col-span-2">
            <Label for="schedule-cron" class="mb-2 text-sm font-medium text-slate-300">Cron expression</Label>
            <Input id="schedule-cron" bind:value={formCron} placeholder="0 0 3 * * ?" class="font-mono! {inputClass}" />
            <p class="mt-1.5 text-xs text-slate-600">Quartz format: seconds minutes hours day-of-month month day-of-week.</p>
          </div>
        {:else}
          <div class="sm:col-span-2">
            <Label for="schedule-interval" class="mb-2 text-sm font-medium text-slate-300">Interval (seconds)</Label>
            <Input id="schedule-interval" type="number" min={1} bind:value={formIntervalSeconds} placeholder="3600" class={inputClass} />
          </div>
        {/if}
      </div>

      <div class="grid gap-4 sm:grid-cols-3">
        <div>
          <Label for="schedule-timezone" class="mb-2 text-sm font-medium text-slate-300">Timezone</Label>
          <Input id="schedule-timezone" required bind:value={formTimezone} placeholder="UTC" class={inputClass} />
          <p class="mt-1.5 text-xs text-slate-600">TZDB id, e.g. UTC or America/Los_Angeles.</p>
        </div>
        <div>
          <Label for="schedule-catchup" class="mb-2 text-sm font-medium text-slate-300">Missed-run policy</Label>
          <Select id="schedule-catchup" items={catchupItems} bind:value={formCatchupPolicy} class={inputClass} />
        </div>
        <div class="flex items-end pb-2">
          <Toggle bind:checked={formEnabled} class="text-sm text-slate-300">Enabled</Toggle>
        </div>
      </div>

      {#if formError}
        <div class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{formError}</span>
        </div>
      {/if}

      <div class="flex justify-end gap-2">
        <Button color="dark" class={outlineButtonClass} onclick={() => (formOpen = false)}>Cancel</Button>
        <Button type="submit" color="blue" class={saveButtonClass} disabled={formSaving || (!editingKey && !formKey.trim())}>
          {#if formSaving}
            <Spinner size="4" class="mr-1.5" />
          {/if}
          {editingKey ? 'Save changes' : 'Create schedule'}
        </Button>
      </div>
    </form>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center"><Spinner size="8" /></div>
  {:else if schedules.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <ClockOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No schedules yet</p>
      <p class="mt-1 text-sm text-slate-500">Create one to run maintenance tasks on a recurring basis.</p>
    </div>
  {:else}
    <div class="mt-5 space-y-2">
      {#each schedules as schedule (schedule.key)}
        <article
          class="flex flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:px-4 lg:flex-row lg:items-center"
        >
          <div class="flex min-w-0 flex-1 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
              <ClockOutline class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-slate-100">{schedule.key}</h3>
                <span class="rounded-full bg-slate-800 px-2 py-0.5 font-mono text-[10px] font-semibold text-slate-400">
                  {schedule.taskType}
                </span>
                {#if !schedule.enabled}
                  <span class="rounded-full bg-amber-950/60 px-2 py-0.5 text-[10px] font-semibold text-amber-300">disabled</span>
                {/if}
              </div>
              <p class="mt-0.5 truncate font-mono text-xs text-slate-400">
                {scheduleTimingSummary(schedule)} · {schedule.timezone} · {schedule.catchupPolicy === 'Coalesce' ? 'coalesce missed runs' : 'skip missed runs'}
              </p>
              <p class="mt-0.5 truncate text-xs text-slate-500">
                Next due {formatDate(schedule.nextDueAt)} · last success {formatDate(schedule.lastSuccessAt)} · last attempt {formatDate(schedule.lastAttemptAt)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 items-center gap-2 lg:ml-auto">
            {#if mutation === `toggle:${schedule.key}`}
              <Spinner size="5" />
            {:else}
              <Toggle
                checked={schedule.enabled}
                disabled={mutation !== null}
                aria-label={`${schedule.enabled ? 'Disable' : 'Enable'} schedule ${schedule.key}`}
                onchange={() => void toggleEnabled(schedule)}
              />
            {/if}
            <button
              type="button"
              class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
              title="Edit schedule"
              aria-label={`Edit schedule ${schedule.key}`}
              onclick={() => openEditForm(schedule)}
            >
              <EditOutline class="h-4 w-4" />
            </button>
            <button
              type="button"
              class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
              title="Delete schedule"
              aria-label={`Delete schedule ${schedule.key}`}
              disabled={mutation === `delete:${schedule.key}`}
              onclick={() => {
                deleteTarget = schedule;
                deleteModalOpen = true;
              }}
            >
              {#if mutation === `delete:${schedule.key}`}
                <Spinner size="4" />
              {:else}
                <TrashBinOutline class="h-4 w-4" />
              {/if}
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete schedule"
  message={deleteTarget ? `Delete schedule "${deleteTarget.key}"? The ${deleteTarget.taskType} task will no longer run automatically.` : ''}
  confirmLabel="Delete schedule"
  onConfirm={async () => {
    if (deleteTarget) {
      await removeSchedule(deleteTarget);
    }
  }}
/>
