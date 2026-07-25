<script lang="ts">
  import NotificationProviderForm from '$lib/components/profile/NotificationProviderForm.svelte';
</script>

<svelte:head>
  <title>New notification provider · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="notification-provider-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="notification-provider-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">
      New notification provider
    </h1>
    <p class="mt-2 text-sm text-base-content/60">
      Connect a delivery channel for notifications. Passwords, tokens, and keys are stored as write-only secrets automatically.
    </p>
  </div>

  <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
    <NotificationProviderForm mode="create" />
  </div>
</section>
