<script lang="ts">
  import OptionPresetForm from '$lib/components/profile/OptionPresetForm.svelte';
</script>

<svelte:head>
  <title>New option preset · FrostStream</title>
</svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="option-preset-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-blue-400">Profile</p>
    <h1 id="option-preset-title" class="mt-2 text-2xl font-bold tracking-tight text-slate-100">New option preset</h1>
    <p class="mt-2 text-sm text-slate-400">
      Store a named set of yt-dlp options that preset-based download requests can reference.
    </p>
  </div>

  <div class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
    <OptionPresetForm mode="create" />
  </div>
</section>
