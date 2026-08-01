<script lang="ts">
  import { onMount } from 'svelte';
  import { CircleAlert } from '@lucide/svelte';
  import NotificationProviderForm from '$lib/components/profile/NotificationProviderForm.svelte';
  import { getNotificationProvider, type NotificationProvider } from '$lib/api/notifications';

  let { params } = $props();

  let provider = $state<NotificationProvider | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  onMount(() => {
    void loadProvider();
  });

  async function loadProvider() {
    loading = true;
    loadError = null;
    try {
      provider = await getNotificationProvider(params.key);
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the notification provider.';
    } finally {
      loading = false;
    }
  }
</script>

<svelte:head>
  <title>{provider?.displayName ?? provider?.providerKey ?? 'Notification provider'} · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="notification-provider-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="notification-provider-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">
      {provider?.displayName?.trim() || provider?.providerKey || 'Notification provider'}
    </h1>
    <p class="mt-2 text-sm text-base-content/60">
      View and update this notification provider, or send a test notification through it.
    </p>
  </div>

  {#if loading}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if loadError}
    <div class="alert alert-error text-sm" role="alert">
      <div class="flex items-start gap-3">
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{loadError}</span>
      </div>
      <a class="btn btn-sm btn-neutral mt-4 text-xs" href="/profile/notifications">
        Back to profile
      </a>
    </div>
  {:else if provider}
    <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
      <NotificationProviderForm mode="update" initial={provider} />
    </div>
  {/if}
</section>
