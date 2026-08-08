<script lang="ts">
  import { onMount } from 'svelte';
  import {
    Bell,
    BellRing,
    CircleAlert,
    Pencil,
    Plus,
    Trash2
  } from '@lucide/svelte';
  import {
    deleteNotificationProvider,
    listNotificationProviders,
    updateNotificationProviderEnabled,
    type NotificationProvider
  } from '$lib/api/notifications';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';

  const cardClass = 'card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6';
  let providers = $state<NotificationProvider[]>([]);
  let providersLoading = $state(true);
  let providersError = $state<string | null>(null);
  let deletingProviderKey = $state<string | null>(null);
  let updatingProviderKey = $state<string | null>(null);
  let deleteModalOpen = $state(false);
  let providerPendingDelete = $state<NotificationProvider | null>(null);

  onMount(() => {
    void loadProviders();
  });

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

  async function toggleProvider(provider: NotificationProvider) {
    updatingProviderKey = provider.providerKey;
    try {
      const updated = await updateNotificationProviderEnabled(provider, !provider.enabled);
      providers = providers.map((item) => item.providerKey === updated.providerKey ? updated : item);
    } catch (err) {
      providersError = err instanceof Error ? err.message : 'Could not update the notification provider.';
    } finally {
      updatingProviderKey = null;
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
    const eventKeys = provider.eventKeys ?? [];
    const eventSummary = eventKeys.length === 0
      ? 'no sources'
      : `${eventKeys.length} source${eventKeys.length === 1 ? '' : 's'}`;
    return [
      provider.providerKind,
      provider.defaultTo ? `to ${provider.defaultTo}` : null,
      eventSummary
    ].filter(Boolean).join(' · ');
  }
</script>

<section class={cardClass} aria-labelledby="notifications-title">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="notifications-title" class="text-base font-bold text-base-content">Notifications</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Manage the providers used to deliver notifications. Passwords, tokens, and keys are
        stored as write-only secrets automatically and removed when a provider is deleted.
      </p>
    </div>
    <a class="btn btn-sm btn-neutral shrink-0" href="/profile/notification-providers/new">
      <Plus class="mr-1.5 h-3.5 w-3.5" />
      New provider
    </a>
  </div>

  <div class="mt-6 flex items-center justify-between gap-3">
    <h3 class="text-sm font-bold text-base-content">Providers</h3>
  </div>

  {#if providersError}
    <div
      class="alert alert-error mt-4 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{providersError}</span>
    </div>
  {/if}

  {#if providersLoading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if providers.length === 0}
    <div class="mt-4 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/30 p-8 text-center">
      <Bell class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No notification providers yet</p>
      <p class="mt-1 text-sm text-base-content/50">Create one to enable delivery through a channel like email or Slack.</p>
    </div>
  {:else}
    <div class="mt-4 space-y-2">
      {#each providers as provider (provider.providerKey)}
        <article
          class="card flex min-h-[4rem] flex-col gap-3 border-[length:var(--border)] border-base-300 bg-base-100 p-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:flex-row sm:items-center"
        >
          <div class="flex min-w-0 flex-1 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-field bg-base-300/70 text-primary">
              {#if provider.enabled}
                <BellRing class="h-4.5 w-4.5" />
              {:else}
                <Bell class="h-4.5 w-4.5" />
              {/if}
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h4 class="truncate text-sm font-semibold text-base-content">{providerLabel(provider)}</h4>
                <span class="badge badge-sm badge-accent text-[10px] text-accent-content">
                  {provider.providerKey}
                </span>
                {#if !provider.enabled}
                  <span class="badge badge-sm badge-warning text-[10px] text-warning-content">Disabled</span>
                {/if}
              </div>
              <p class="mt-0.5 truncate text-xs text-base-content/60">{providerSummary(provider)}</p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <label class="flex items-center gap-2 px-2 text-xs text-base-content/70" title={provider.enabled ? 'Disable provider' : 'Enable provider'}>
              <span class="sr-only">{provider.enabled ? 'Disable' : 'Enable'} {providerLabel(provider)}</span>
              <input
                type="checkbox"
                class="toggle toggle-primary toggle-sm"
                checked={provider.enabled}
                disabled={updatingProviderKey === provider.providerKey}
                onchange={() => void toggleProvider(provider)}
              />
            </label>
            <a
              href={`/profile/notification-providers/${encodeURIComponent(provider.providerKey)}`}
              class="btn btn-sm btn-neutral text-xs"
              aria-label={`Edit notification provider ${provider.providerKey}`}
            >
              <Pencil class="h-4 w-4" />
              Edit
            </a>
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Delete provider"
              aria-label={`Delete notification provider ${provider.providerKey}`}
              disabled={deletingProviderKey === provider.providerKey}
              onclick={() => requestRemoveProvider(provider)}
            >
              {#if deletingProviderKey === provider.providerKey}
                <span class="loading loading-spinner loading-xs"></span>
              {:else}
                <Trash2 class="mr-1.5 h-4 w-4" />
              {/if}
              Delete
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
