<script lang="ts">
import { Code2, Palette } from '@lucide/svelte';
import { customCssEnabled, theme, themes, themeLabels, setCustomCssEnabled, setTheme } from '$lib/stores/theme';
</script>

<svelte:head>
  <title>Appearance · Profile · FrostStream</title>
</svelte:head>

<section class="max-w-3xl" aria-labelledby="appearance-title">
  <h2 id="appearance-title" class="flex items-center gap-2 text-lg font-semibold text-base-content">
    <Palette class="h-5 w-5" />
    Appearance
  </h2>
  <p class="mt-1 text-sm text-base-content/60">Choose how FrostStream looks on this device.</p>

  <div class="mt-5 grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4">
    {#each themes as option (option)}
      <label
        data-theme={option}
        class={[
          'flex cursor-pointer flex-col gap-2.5 rounded-box border bg-base-100 p-2.5 text-left transition hover:-translate-y-0.5 hover:shadow-md',
          !$customCssEnabled && $theme === option
            ? 'border-primary ring-2 ring-primary ring-offset-2 ring-offset-base-200'
            : 'border-base-300 hover:border-primary/60'
        ]}
      >
        <div class="flex w-full items-center justify-between gap-3">
          <span class="truncate text-xs font-semibold text-base-content">{themeLabels[option]}</span>
          <input
            type="radio"
            name="theme"
            class="radio radio-primary radio-sm shrink-0"
            value={option}
            checked={!$customCssEnabled && $theme === option}
            onchange={() => setTheme(option)}
          />
        </div>

        <div class="w-full rounded-box border border-base-300 bg-base-200 p-1.5">
          <div class="grid grid-cols-4 gap-1.5">
            <span class="grid h-6 place-items-center rounded-field bg-primary text-xs font-bold text-primary-content" title="Primary">A</span>
            <span class="grid h-6 place-items-center rounded-field bg-secondary text-xs font-bold text-secondary-content" title="Secondary">A</span>
            <span class="grid h-6 place-items-center rounded-field bg-accent text-xs font-bold text-accent-content" title="Accent">A</span>
            <span class="grid h-6 place-items-center rounded-field bg-neutral text-xs font-bold text-neutral-content" title="Neutral">A</span>
          </div>
          <div class="mt-1.5 grid grid-cols-3 gap-1.5">
            <span class="h-2 rounded-full bg-base-300"></span>
            <span class="h-2 rounded-full bg-base-content"></span>
            <span class="h-2 rounded-full bg-info"></span>
          </div>
        </div>
      </label>
    {/each}

    <div class={[
      'flex cursor-pointer flex-col gap-2.5 rounded-box border bg-base-100 p-2.5 text-left transition hover:-translate-y-0.5 hover:shadow-md',
      $customCssEnabled
        ? 'border-primary ring-2 ring-primary ring-offset-2 ring-offset-base-200'
        : 'border-base-300 hover:border-primary/60'
    ]} role="radio" aria-checked={$customCssEnabled} tabindex="0" onclick={() => setCustomCssEnabled(true)} onkeydown={(event) => {
      if (event.key === 'Enter' || event.key === ' ') setCustomCssEnabled(true);
    }}>
      <div class="flex w-full items-center justify-between gap-3">
        <span class="flex min-w-0 items-center gap-2 truncate text-xs font-semibold text-base-content">
          
          Custom CSS
        </span>
        <div class="flex shrink-0 items-center gap-2">
          <a class="btn btn-sm btn-neutral px-2 text-xs" href="/profile/appearance/custom-css" onclick={(event) => event.stopPropagation()}>Edit</a>
          <input type="radio" name="theme" class="radio radio-primary radio-sm" checked={$customCssEnabled} onchange={() => setCustomCssEnabled(true)} aria-label="Use Custom CSS" />
        </div>
      </div>
      <div class="w-full rounded-box border border-base-300 bg-base-200 p-1.5">
        <div class="grid grid-cols-4 gap-1.5">
          <span class="grid h-6 place-items-center rounded-field bg-primary text-xs font-bold text-primary-content" title="Primary">A</span>
          <span class="grid h-6 place-items-center rounded-field bg-secondary text-xs font-bold text-secondary-content" title="Secondary">A</span>
          <span class="grid h-6 place-items-center rounded-field bg-accent text-xs font-bold text-accent-content" title="Accent">A</span>
          <span class="grid h-6 place-items-center rounded-field bg-neutral text-xs font-bold text-neutral-content" title="Neutral">A</span>
        </div>
        <div class="mt-1.5 grid grid-cols-3 gap-1.5">
          <span class="h-2 rounded-full bg-base-300"></span>
          <span class="h-2 rounded-full bg-base-content"></span>
          <span class="h-2 rounded-full bg-info"></span>
        </div>
      </div>
      <p class="text-xs leading-5 text-base-content/60">Customize this browser’s appearance with CSS stored locally on this device.</p>
    </div>
  </div>
</section>
