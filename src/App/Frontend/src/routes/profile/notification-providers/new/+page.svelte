<script lang="ts">
  import NotificationProviderForm from '$lib/components/profile/NotificationProviderForm.svelte';
</script>

<svelte:head>
  <title>New notification provider · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="notification-provider-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-blue-400">Profile</p>
    <h1 id="notification-provider-title" class="mt-2 text-2xl font-bold tracking-tight text-slate-100">
      New notification provider
    </h1>
    <p class="mt-2 text-sm text-slate-400">
      Connect a delivery channel for notifications. Passwords, tokens, and keys are stored as write-only secrets automatically.
    </p>
  </div>

  <div class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
    <NotificationProviderForm mode="create" />
  </div>
</section>
