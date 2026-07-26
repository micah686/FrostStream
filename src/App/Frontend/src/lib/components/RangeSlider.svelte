<script lang="ts">
  interface Props {
    id?: string;
    min?: number;
    max?: number;
    step?: number;
    value: number;
    disabled?: boolean;
  }

  let { id, min = 0, max = 100, step = 1, value = $bindable(), disabled = false }: Props = $props();

  const fillPercent = $derived(max === min ? 0 : ((value - min) / (max - min)) * 100);
</script>

<input
  {id}
  type="range"
  {min}
  {max}
  {step}
  {disabled}
  bind:value
  style="--range-fill: {fillPercent}%"
  class="range-trail h-2 w-full cursor-pointer appearance-none rounded-full bg-base-300 disabled:cursor-not-allowed disabled:opacity-60"
/>

<style>
  /* Chromium/WebKit has no ::-moz-range-progress equivalent, so paint the
     filled trail as a gradient stopped at the current value. */
  .range-trail {
    background:
      linear-gradient(
        to right,
        var(--color-primary) 0%,
        var(--color-primary) var(--range-fill),
        var(--color-base-300) var(--range-fill),
        var(--color-base-300) 100%
      );
  }

  /* appearance-none drops the native thumb, so it has to be rebuilt. */
  .range-trail::-webkit-slider-thumb {
    -webkit-appearance: none;
    appearance: none;
    height: 1.125rem;
    width: 1.125rem;
    border: 2px solid var(--color-base-100);
    border-radius: 9999px;
    background: var(--color-primary);
    box-shadow: 0 1px 3px rgb(0 0 0 / 40%);
  }

  .range-trail::-moz-range-thumb {
    height: 1.125rem;
    width: 1.125rem;
    border: 2px solid var(--color-base-100);
    border-radius: 9999px;
    background: var(--color-primary);
    box-shadow: 0 1px 3px rgb(0 0 0 / 40%);
  }
</style>
