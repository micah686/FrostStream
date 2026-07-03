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
  class="range-trail h-2 w-full cursor-pointer appearance-none rounded-full bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
/>

<style>
  /* Chromium/WebKit has no ::-moz-range-progress equivalent, so paint the
     filled trail as a gradient stopped at the current value. */
  .range-trail {
    background:
      linear-gradient(
        to right,
        var(--color-frost-500) 0%,
        var(--color-frost-500) var(--range-fill),
        #1e293b var(--range-fill),
        #1e293b 100%
      );
  }
</style>
