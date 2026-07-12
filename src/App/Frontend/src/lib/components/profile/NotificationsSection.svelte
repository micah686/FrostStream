<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Spinner, Toggle } from 'flowbite-svelte';
  import {
    BellOutline,
    BellRingOutline,
    EditOutline,
    ExclamationCircleOutline,
    PlusOutline,
    RefreshOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import {
    deleteNotificationProvider,
    getNotificationPreferences,
    listNotificationProviders,
    updateNotificationPreferences,
    type NotificationPreferences,
    type NotificationProvider
  } from '$lib/api/notifications';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800! disabled:opacity-60';
  const rowActionClass =
    'inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-50';

  let preferences = $state<NotificationPreferences | null>(null);
  let preferencesLoading = $state(true);
  let preferencesError = $state<string | null>(null);
  let savingPreferences = $state(false);

  let providers = $state<NotificationProvider[]>([]);
  let providersLoading = $state(true);
  let providersError = $state<string | null>(null);
  let deletingProviderKey = $state<string | null>(null);
  let deleteModalOpen = $state(false);
  let providerPendingDelete = $state<NotificationProvider | null>(null);

  onMount(() => {
    void loadPreferences();
    void loadProviders();
  });

  async function loadPreferences() {
    preferencesLoading = true;
    preferencesError = null;
    try {
      preferences = await getNotificationPreferences();
    } catch (err) {
      preferencesError = err instanceof Error ? err.message : 'Could not load notification preferences.';
    } finally {
      preferencesLoading = false;
    }
  }

  async function loadProviders() {
    providersLoading = true;
    providersError = null;
    try {
      providers = sortProviders(await listNotificationProviders());
    } catch (err) {
      providersError = err instanceof Error ? err.message : 'Could not load notification providers.';
    } finally {
      providersLoading = false;
    }
  }

  async function toggleEnabled() {
    if (!preferences) {
      return;
    }

    const nextEnabled = !preferences.enabled;
    savingPreferences = true;
    preferencesError = null;
    try {
      preferences = await updateNotificationPreferences({ ...preferences, enabled: nextEnabled });
    } catch (err) {
      preferencesError = err instanceof Error ? err.message : 'Could not update notification preferences.';
    } finally {
      savingPreferences = false;
    }
  }

  function requestRemoveProvider(provider: NotificationProvider) {
    providerPendingDelete = provider;
    deleteModalOpen = true;
  }

  async function confirmRemoveProvider() {
    const provider = providerPendingDelete;
    if (!provider) {
      return;
    }

    deletingProviderKey = provider.providerKey;
    providersError = null;
    try {
      await deleteNotificationProvider(provider.providerKey);
      providers = providers.filter((item) => item.providerKey !== provider.providerKey);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Could not delete the notification provider.';
      providersError = message;
      throw new Error(message);
    } finally {
      deletingProviderKey = null;
    }
  }

  function sortProviders(items: NotificationProvider[]): NotificationProvider[] {
    return [...items].sort((a, b) => a.providerKey.localeCompare(b.providerKey));
  }

  function providerLabel(provider: NotificationProvider): string {
    return provider.displayName?.trim() || provider.providerKey;
  }

  function providerSummary(provider: NotificationProvider): string {
    return [
      provider.providerKind,
      provider.defaultTo ? `to ${provider.defaultTo}` : null,
      provider.enabled ? 'enabled' : 'disabled'
    ].filter(Boolean).join(' · ');
  }
</script>

<section class={cardClass} aria-labelledby="notifications-title">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="notifications-title" class="text-base font-bold text-slate-100">Notifications</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Turn notifications on or off, and manage the providers used to deliver them. Passwords, tokens, and keys are
        stored as write-only secrets automatically and removed when a provider is deleted.
      </p>
    </div>
  </div>

  {#if preferencesError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{preferencesError}</span>
    </div>
  {/if}

  <div class="mt-5 flex items-center justify-between gap-4 rounded-xl border border-slate-800/80 bg-slate-950/20 p-4">
    <div class="min-w-0">
      <p class="text-sm font-semibold text-slate-100">Enable notifications</p>
      <p class="mt-0.5 text-xs text-slate-500">
        When off, no events are delivered through any provider below, regardless of their individual enabled state.
      </p>
    </div>
    {#if preferencesLoading}
      <Spinner size="5" />
    {:else}
      <Toggle
        checked={preferences?.enabled ?? false}
        disabled={savingPreferences || !preferences}
        onchange={() => void toggleEnabled()}
      />
    {/if}
  </div>

  <div class="mt-6 flex items-center justify-between gap-3">
    <h3 class="text-sm font-bold text-slate-100">Providers</h3>
    <div class="flex shrink-0 gap-2">
      <Button color="dark" class={outlineButtonClass} disabled={providersLoading} onclick={() => void loadProviders()}>
        <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </Button>
      <Button href="/profile/notification-providers/new" color="dark" class={outlineButtonClass}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        New provider
      </Button>
    </div>
  </div>

  {#if providersError}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{providersError}</span>
    </div>
  {/if}

  {#if providersLoading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if providers.length === 0}
    <div class="mt-4 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <BellOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No notification providers yet</p>
      <p class="mt-1 text-sm text-slate-500">Create one to enable delivery through a channel like email or Slack.</p>
    </div>
  {:else}
    <div class="mt-4 space-y-2">
      {#each providers as provider (provider.providerKey)}
        <article
          class="flex min-h-[4rem] flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:flex-row sm:items-center"
        >
          <div class="flex min-w-0 flex-1 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
              {#if provider.enabled}
                <BellRingOutline class="h-4.5 w-4.5" />
              {:else}
                <BellOutline class="h-4.5 w-4.5" />
              {/if}
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h4 class="truncate text-sm font-semibold text-slate-100">{providerLabel(provider)}</h4>
                <Badge rounded color="gray" class="bg-slate-800! px-2! py-0.5! text-[10px]! text-slate-400!">
                  {provider.providerKey}
                </Badge>
              </div>
              <p class="mt-0.5 truncate text-xs text-slate-400">{providerSummary(provider)}</p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <a
              href={`/profile/notification-providers/${encodeURIComponent(provider.providerKey)}`}
              class={rowActionClass}
              aria-label={`Edit notification provider ${provider.providerKey}`}
            >
              <EditOutline class="h-4 w-4" />
              Edit
            </a>
            <button
              type="button"
              class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-2.5 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
              title="Delete provider"
              aria-label={`Delete notification provider ${provider.providerKey}`}
              disabled={deletingProviderKey === provider.providerKey}
              onclick={() => requestRemoveProvider(provider)}
            >
              {#if deletingProviderKey === provider.providerKey}
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
  title="Delete notification provider"
  message={providerPendingDelete
    ? `Delete notification provider "${providerLabel(providerPendingDelete)}"? Its stored secrets will also be removed.`
    : ''}
  confirmLabel="Delete"
  onConfirm={confirmRemoveProvider}
/>
