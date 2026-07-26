<script lang="ts">
  import { Palette } from '@lucide/svelte';
  import { theme, themes, themeLabels, setTheme } from '$lib/stores/theme';
</script>

<svelte:head>
  <title>Appearance · Profile · FrostStream</title>
</svelte:head>

<section class="max-w-xl" aria-labelledby="appearance-title">
  <h2 id="appearance-title" class="flex items-center gap-2 text-lg font-semibold text-base-content">
    <Palette class="h-5 w-5" />
    Appearance
  </h2>
  <p class="mt-1 text-sm text-base-content/60">Choose how FrostStream looks on this device.</p>

  <div class="mt-5 grid grid-cols-2 gap-3">
    {#each themes as option (option)}
      <label
        class={[
          'flex cursor-pointer flex-col items-center gap-2 rounded-xl border p-4 transition',
          $theme === option
            ? 'border-primary bg-primary/10'
            : 'border-base-300/80 bg-base-200/40 hover:bg-base-200/70'
        ]}
      >
        <input
          type="radio"
          name="theme"
          class="radio radio-primary radio-sm"
          value={option}
          checked={$theme === option}
          onchange={() => setTheme(option)}
        />
        <span class="text-sm font-medium text-base-content">{themeLabels[option]}</span>
      </label>
    {/each}
  </div>
</section>
