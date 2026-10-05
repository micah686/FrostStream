<script lang="ts">
  import { Chart, type ChartConfiguration } from 'chart.js/auto';

  let {
    config,
    ariaLabel,
    height = '18rem'
  }: {
    config: ChartConfiguration;
    ariaLabel: string;
    height?: string;
  } = $props();

  let canvas: HTMLCanvasElement;

  $effect(() => {
    const nextConfig = config;
    if (!canvas) return;

    const chart = new Chart(canvas, nextConfig);
    return () => chart.destroy();
  });
</script>

<div class="relative w-full" style={`height: ${height}`} role="img" aria-label={ariaLabel}>
  <canvas bind:this={canvas} aria-hidden="true">
    {ariaLabel}
  </canvas>
</div>
