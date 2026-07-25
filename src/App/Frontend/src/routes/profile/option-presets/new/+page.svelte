<script lang="ts">
  import OptionPresetForm from '$lib/components/profile/OptionPresetForm.svelte';
</script>

<svelte:head>
  <title>New option preset · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="option-preset-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="option-preset-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">New option preset</h1>
    <p class="mt-2 text-sm text-base-content/60">
      Store a named set of yt-dlp options that preset-based download requests can reference.
    </p>
  </div>

  <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
    <OptionPresetForm mode="create" />
  </div>
</section>
