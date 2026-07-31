<script lang="ts">
  import { Palette } from '@lucide/svelte';
  import { theme, themes, themeLabels, setTheme } from '$lib/stores/theme';
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
        class={[
          'flex cursor-pointer flex-col items-center gap-2 rounded-xl border p-4 transition',
          $theme === option
            ? 'border-primary bg-primary text-primary-content shadow-sm ring-1 ring-primary'
            : 'border-base-content/25 bg-base-100 hover:border-base-content/45 hover:bg-base-200'
        ]}
      >
        <input
          type="radio"
          name="theme"
          class={[
            'radio radio-sm',
            $theme === option
              ? 'border-primary-content bg-primary-content/10 text-primary-content checked:border-primary-content checked:bg-primary-content checked:text-primary'
              : 'radio-primary'
          ]}
          value={option}
          checked={$theme === option}
          onchange={() => setTheme(option)}
        />
        <span class={$theme === option ? 'text-sm font-semibold text-primary-content' : 'text-sm font-medium text-base-content'}>
          {themeLabels[option]}
        </span>
      </label>
    {/each}
  </div>
</section>
